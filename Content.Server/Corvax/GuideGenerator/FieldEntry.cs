using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server.Corvax.GuideGenerator;

public static class FieldEntry
{
    private static readonly Regex DoubleEntryRegex = new(@"^[+-]?\d+\.\d+$");

    private enum TypeCategory
    {
        Object,
        String,
        Collection,
        ConcreteClass,
        ValueType,
        AbstractOrInterface
    }

    public static object? ProcessNode(object instance, MappingDataNode node, MappingDataNode? composed = null)
    {
        NormalizeFlagsToSequences(instance, node);
        SupplementReadOnlyFields(instance.GetType(), node, composed);
        return DataNodeToObject(node);
    }

    public static object? ComputePrototypeDefault(Type kind, ISerializationManager serializationManager)
    {
        var instance = TryCreateInstance(kind);
        if (instance == null)
            return null;

        try
        {
            EnsureFieldsCollectionsInitialized(instance);
            if (!TryWriteValueAsMapping(serializationManager, kind, instance, out var node, true))
                return new Dictionary<string, object?>();

            node.Remove("id");
            NormalizeFlagsToSequences(instance, node);
            return DataNodeToObject(node);
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    public static object? ComputeComponentDefault(string componentName, IComponentFactory componentFactory, ISerializationManager serializationManager)
    {
        if (!componentFactory.TryGetRegistration(componentName, out var registration))
            return null;

        try
        {
            var component = componentFactory.GetComponent(registration.Type);
            EnsureFieldsCollectionsInitialized(component);
            if (!TryWriteValueAsMapping(serializationManager, component.GetType(), component, out var node, true))
                return new Dictionary<string, object?>();

            NormalizeFlagsToSequences(component, node);
            return DataNodeToObject(node);
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    public static bool TryWriteValueAsMapping(ISerializationManager serializationManager, Type type, object value, out MappingDataNode node, bool alwaysWrite = false)
    {
        try
        {
            node = serializationManager.WriteValueAs<MappingDataNode>(type, value, alwaysWrite);
            return true;
        }
        catch
        {
            node = new MappingDataNode();
            return false;
        }
    }

    public static Dictionary<string, object?> DeduplicateAgainstDefault(object? defaultObj, Dictionary<string, object?> map)
    {
        var defaults = defaultObj as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        foreach (var fields in map.Values)
        {
            if (fields is Dictionary<string, object?> entityFields)
                RemoveDefaultDuplicates(defaults, entityFields);
        }

        return new Dictionary<string, object?>
        {
            ["default"] = defaultObj,
            ["id"] = map
        };
    }

    public static object? DataNodeToObject(DataNode node)
    {
        if (node is MappingDataNode mapping)
            return ConvertMapping(mapping);

        if (node is SequenceDataNode sequence)
            return ConvertSequence(sequence);

        if (node is ValueDataNode value)
            return ConvertValue(value);

        return node.ToString();
    }

    public static void SupplementReadOnlyFields(Type type, MappingDataNode serialized, MappingDataNode? composed)
    {
        if (composed == null)
            return;

        foreach (var member in GetSerializedMembers(type))
        {
            if (!member.Attribute!.ReadOnly)
                continue;

            if (!serialized.Has(member.Tag) && composed.Has(member.Tag))
                serialized[member.Tag] = composed[member.Tag].Copy();
        }
    }

    public static void NormalizeFlagsToSequences(object instance, MappingDataNode node)
    {
        var members = GetSerializedMembers(instance.GetType()).ToArray();
        foreach (var key in node.Keys.ToList())
        {
            var member = members.FirstOrDefault(x => string.Equals(x.Tag, key, StringComparison.OrdinalIgnoreCase));
            if (member == null || !member.Type.IsEnum || member.Type.GetCustomAttribute<FlagsAttribute>(false) == null)
                continue;

            var value = member.GetValue(instance);
            if (value == null)
                continue;

            var numericValue = Convert.ToInt64(value);
            var names = Enum.GetValues(member.Type).Cast<object>()
                .Select(flag => (Name: Enum.GetName(member.Type, flag)!, Value: Convert.ToInt64(flag)))
                .Where(flag => flag.Value != 0 && (flag.Value & (flag.Value - 1)) == 0 && (numericValue & flag.Value) != 0)
                .Select(flag => flag.Name)
                .ToArray();

            node[key] = new SequenceDataNode(names);
        }
    }

    public static void EnsureFieldsCollectionsInitialized(object instance)
    {
        if (CanSafelyInitializeDefault(instance, new HashSet<Type>()))
            InitializeMembers(instance);
    }

    public static Type GetMemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => throw new ArgumentException($"Unsupported member type: {member.GetType()}", nameof(member))
    };

    private static object? ConvertMapping(MappingDataNode mapping)
    {
        var result = mapping.ToDictionary(pair => pair.Key, pair => DataNodeToObject(pair.Value));
        return mapping.Tag == null ? result : new Dictionary<string, object?> { [mapping.Tag] = result };
    }

    private static object ConvertSequence(SequenceDataNode sequence)
    {
        var items = sequence.Select(DataNodeToObject).ToList();
        var typedMap = new Dictionary<string, object?>();
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> dictionary || !dictionary.TryGetValue("type", out var type) || type == null)
                return items;

            var clone = new Dictionary<string, object?>(dictionary);
            clone.Remove("type");
            typedMap[$"type:{type}"] = clone;
        }

        return typedMap.Count > 0 ? typedMap : items;
    }

    private static object? ConvertValue(ValueDataNode value)
    {
        if (value.IsNull)
            return null;

        var raw = value.Value;
        object parsed = bool.TryParse(raw, out var boolean) ? boolean
            : int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer
            : DoubleEntryRegex.IsMatch(raw) && double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number) ? number
            : raw;

        if (value.Tag == null)
            return parsed;

        return new Dictionary<string, object?>
        {
            [value.Tag] = string.IsNullOrEmpty(raw) ? new Dictionary<string, object?>() : parsed
        };
    }

    private static bool AreEqual(object? first, object? second)
    {
        if (first is null || second is null)
            return first is null && second is null;

        if (first is IDictionary<string, object?> firstDictionary && second is IDictionary<string, object?> secondDictionary)
            return firstDictionary.Count == secondDictionary.Count && firstDictionary.All(pair => secondDictionary.TryGetValue(pair.Key, out var value) && AreEqual(pair.Value, value));

        if (first is IList firstList && second is IList secondList)
            return firstList.Count == secondList.Count && Enumerable.Range(0, firstList.Count).All(index => AreEqual(firstList[index], secondList[index]));

        return first.Equals(second);
    }

    private static void RemoveDefaultDuplicates(Dictionary<string, object?> defaults, Dictionary<string, object?> target)
    {
        foreach (var key in target.Keys.ToList())
        {
            if (!defaults.TryGetValue(key, out var defaultValue))
                continue;

            var value = target[key];
            if (AreEqual(defaultValue, value))
                target.Remove(key);
            else if (defaultValue is Dictionary<string, object?> defaultMap && value is Dictionary<string, object?> targetMap)
                RemoveDefaultDuplicates(defaultMap, targetMap);
        }
    }

    private static bool CanSafelyInitializeDefault(object instance, HashSet<Type> activeTypes)
    {
        var type = instance.GetType();
        if (!activeTypes.Add(type))
            return false;

        try
        {
            foreach (var member in GetWritableMembers(type))
            {
                if (member.GetValue(instance) != null)
                    continue;

                if (!CanSafelyInitializeMember(member.Type, activeTypes))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }

    private static bool CanSafelyInitializeMember(Type type, HashSet<Type> activeTypes) => ClassifyType(type) switch
    {
        TypeCategory.Object => false,
        TypeCategory.String or TypeCategory.Collection or TypeCategory.ValueType => true,
        TypeCategory.ConcreteClass => CanSafelyInitializeConcrete(type, activeTypes),
        TypeCategory.AbstractOrInterface => CanSafelyInitializeAbstract(type, activeTypes),
        _ => false
    };

    private static bool CanSafelyInitializeConcrete(Type type, HashSet<Type> activeTypes)
    {
        var instance = TryCreateInstance(type);
        return instance != null && !activeTypes.Contains(type) && CanSafelyInitializeDefault(instance, activeTypes);
    }

    private static bool CanSafelyInitializeAbstract(Type type, HashSet<Type> activeTypes)
    {
        var concrete = FindConcreteAssignableType(type);
        return concrete == null || !activeTypes.Contains(concrete) && CanSafelyInitializeConcrete(concrete, activeTypes);
    }

    private static void InitializeMembers(object instance)
    {
        foreach (var member in GetWritableMembers(instance.GetType()))
        {
            if (member.GetValue(instance) != null || !TryCreateDefaultValue(member.Type, out var value, out var recurse) || value == null)
                continue;

            try
            {
                member.SetValue(instance, value);
                if (recurse)
                    InitializeMembers(value);
            }
            catch
            {
                // Some serialized members intentionally reject reflective assignment.
            }
        }
    }

    private static bool TryCreateDefaultValue(Type type, out object? value, out bool recurse)
    {
        value = null;
        recurse = false;

        switch (ClassifyType(type))
        {
            case TypeCategory.String:
                value = string.Empty;
                return true;
            case TypeCategory.Collection:
                value = type.IsArray ? Array.CreateInstance(type.GetElementType()!, 0) : TryCreateInstance(type);
                return value != null;
            case TypeCategory.ConcreteClass:
                value = TryCreateInstance(type);
                recurse = value != null;
                return value != null;
            case TypeCategory.AbstractOrInterface:
                value = FindConcreteAssignableType(type) is { } concrete ? TryCreateInstance(concrete) : null;
                recurse = value != null;
                return value != null;
            default:
                return false;
        }
    }

    private static TypeCategory ClassifyType(Type type)
    {
        if (type == typeof(object))
            return TypeCategory.Object;

        if (type == typeof(string))
            return TypeCategory.String;

        if (IsConcreteCollectionLike(type))
            return TypeCategory.Collection;

        if (type.IsClass && !type.IsAbstract)
            return TypeCategory.ConcreteClass;

        if (!type.IsAbstract && !type.IsInterface)
            return TypeCategory.ValueType;

        return TypeCategory.AbstractOrInterface;
    }

    private static object? TryCreateInstance(Type type) =>
        type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null
            ? null
            : Activator.CreateInstance(type, true);

    private static bool IsConcreteCollectionLike(Type type) =>
        !type.IsAbstract && !type.IsInterface &&
        (typeof(IDictionary).IsAssignableFrom(type) || typeof(IList).IsAssignableFrom(type) || type.IsArray || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>));

    private static Type? FindConcreteAssignableType(Type target)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).Cast<Type>().ToArray();
            }

            var candidate = types.FirstOrDefault(type => !type.IsAbstract && !type.IsInterface && target.IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null);
            if (candidate != null)
                return candidate;
        }

        return null;
    }

    private static IEnumerable<SerializedMember> GetSerializedMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        foreach (var field in type.GetFields(flags))
        {
            var attribute = field.GetCustomAttribute<DataFieldAttribute>();
            if (attribute != null)
                yield return new SerializedMember(field, attribute.Tag ?? LowerFirst(field.Name), field.FieldType, attribute);
        }

        foreach (var property in type.GetProperties(flags))
        {
            var attribute = property.GetCustomAttribute<DataFieldAttribute>();
            if (attribute != null && property.GetGetMethod(true) != null)
                yield return new SerializedMember(property, attribute.Tag ?? LowerFirst(property.Name), property.PropertyType, attribute);
        }
    }

    private static IEnumerable<SerializedMember> GetWritableMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in type.GetFields(flags))
        {
            if (!field.IsInitOnly)
                yield return new SerializedMember(field, string.Empty, field.FieldType, null);
        }

        foreach (var property in type.GetProperties(flags))
        {
            if (property.CanWrite && property.GetIndexParameters().Length == 0)
                yield return new SerializedMember(property, string.Empty, property.PropertyType, null);
        }
    }

    private static string LowerFirst(string value) => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);

    private sealed class SerializedMember
    {
        internal SerializedMember(MemberInfo member, string tag, Type type, DataFieldAttribute? attribute)
        {
            Member = member;
            Tag = tag;
            Type = type;
            Attribute = attribute;
        }

        internal MemberInfo Member { get; }
        internal string Tag { get; }
        internal Type Type { get; }
        internal DataFieldAttribute? Attribute { get; }

        internal object? GetValue(object instance) => Member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property => property.GetValue(instance),
            _ => null
        };

        internal void SetValue(object instance, object value)
        {
            switch (Member)
            {
                case FieldInfo field:
                    field.SetValue(instance, value);
                    break;
                case PropertyInfo property:
                    property.SetValue(instance, value);
                    break;
            }
        }
    }
}
