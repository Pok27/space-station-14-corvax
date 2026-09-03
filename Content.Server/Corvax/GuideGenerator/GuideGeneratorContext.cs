using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Robust.Shared.ContentPack;
using Robust.Shared.Configuration;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Utility;

namespace Content.Server.Corvax.GuideGenerator;

internal sealed class GuideGeneratorContext
{
    internal GuideGeneratorContext(
        IResourceManager resourceManager,
        IPrototypeManager prototypeManager,
        ISerializationManager serializationManager,
        IComponentFactory componentFactory,
        ILocalizationManager localization,
        IConfigurationManager configuration,
        ResPath destination)
    {
        ResourceManager = resourceManager;
        PrototypeManager = prototypeManager;
        SerializationManager = serializationManager;
        ComponentFactory = componentFactory;
        Localization = localization;
        Configuration = configuration;
        Destination = destination;
    }

    internal IResourceManager ResourceManager { get; }
    internal IPrototypeManager PrototypeManager { get; }
    internal ISerializationManager SerializationManager { get; }
    internal IComponentFactory ComponentFactory { get; }
    internal ILocalizationManager Localization { get; }
    internal IConfigurationManager Configuration { get; }
    internal ResPath Destination { get; }
}

internal static class GuideJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static void Write(Stream stream, object? value) => JsonSerializer.Serialize(stream, value, Options);

    internal static void WriteFile(IResourceManager resources, ResPath path, object? value)
    {
        using var stream = resources.UserData.OpenWrite(path);
        Write(stream, value);
    }
}
