using System.IO;
using System.Linq;
using Robust.Shared.Localization;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Utility;

namespace Content.Server.Corvax.GuideGenerator;

public static class ComponentJsonGenerator
{
    public static void PublishAll(IResourceManager res, ResPath destRoot)
    {
        PublishAll(new GuideGeneratorContext(
            res,
            IoCManager.Resolve<IPrototypeManager>(),
            IoCManager.Resolve<ISerializationManager>(),
            IoCManager.Resolve<IComponentFactory>(),
            IoCManager.Resolve<ILocalizationManager>(),
            IoCManager.Resolve<IConfigurationManager>(),
            destRoot));
    }

    internal static void PublishAll(GuideGeneratorContext context)
    {
        var res = context.ResourceManager;
        var proto = context.PrototypeManager;
        var ser = context.SerializationManager;
        var compFactory = context.ComponentFactory;
        var destRoot = new ResPath("component").ToRootedPath();

        // Map: component name -> (entity id -> component fields)
        var output = new Dictionary<string, Dictionary<string, object?>>();

        foreach (var p in proto.EnumeratePrototypes(typeof(EntityPrototype)))
        {
            if (p is not EntityPrototype entProto)
                continue;

            foreach (var (compName, componentFields) in BuildEntityComponentMap(entProto, proto, ser, compFactory))
            {
                GetOrCreateEntry(output, compName)[entProto.ID] = componentFields;
            }
        }

        if (output.Count == 0)
            return;

        res.UserData.CreateDir(destRoot);
        foreach (var (compName, map) in output)
        {
            var defaultObj = FieldEntry.ComputeComponentDefault(compName, compFactory, ser);
            var outObj = FieldEntry.DeduplicateAgainstDefault(defaultObj, map);
            var directoryName = TextTools.CapitalizeString(compName);
            var componentRoot = destRoot / directoryName;

            res.UserData.CreateDir(componentRoot);
            GuideJson.WriteFile(res, destRoot / (directoryName + ".json"), outObj);
            GuideJson.WriteFile(res, componentRoot / "defaultFields.json", defaultObj);
        }
    }

    private static Dictionary<string, object?> GetOrCreateEntry(Dictionary<string, Dictionary<string, object?>> output, string key)
    {
        if (!output.TryGetValue(key, out var map))
        {
            map = new Dictionary<string, object?>();
            output[key] = map;
        }

        return map;
    }

    public static Dictionary<string, object?> BuildEntityComponentMap(EntityPrototype entProto, IPrototypeManager proto, ISerializationManager ser, IComponentFactory compFactory)
    {
        var components = new Dictionary<string, object?>(StringComparer.Ordinal);
        var composedComponents = YAMLEntry.GetComposedComponentMappings(entProto, proto, ser, compFactory);

        foreach (var (compName, entry) in entProto.Components)
        {
            if (!FieldEntry.TryWriteValueAsMapping(ser, entry.Component.GetType(), entry.Component, out var node))
                continue;

            composedComponents.TryGetValue(compName, out var composedNode);
            components[compName] = FieldEntry.ProcessNode(entry.Component, node, composedNode);
        }

        foreach (var (compName, node) in composedComponents)
        {
            if (entProto.Components.ContainsKey(compName))
                continue;

            components[compName] = FieldEntry.DataNodeToObject(node);
        }

        return components;
    }
}
