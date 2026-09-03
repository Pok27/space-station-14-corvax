using System;
using System.IO;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server.Corvax.GuideGenerator;

internal sealed class GuideDataGenerator
{
    private readonly GuideGeneratorContext _context;

    internal GuideDataGenerator(GuideGeneratorContext context)
    {
        _context = context;
    }

    internal void Generate()
    {
        WriteFile("entity_prototypes.json", EntityJsonGenerator.PublishJson);
        WriteFile("entity_parent.json", EntityParentJsonGenerator.PublishJson);
        WriteFile("loc.json", LocJsonGenerator.PublishJson);
        WriteFile("meta_license.json", MetaLicenseGenerator.PublishJson);
        WriteFile("prototype.json", PrototypeListGenerator.PublishJson);
        WriteFile("component.json", ComponentListGenerator.PublishJson);
        WriteFile("prototype_store.json", PrototypeStoreGenerator.PublishJson);
        WriteFile("component_store.json", ComponentStoreGenerator.PublishJson);
        WriteFile("entity_project.json", EntityProjectGenerator.PublishJson);
        WriteFile("entity_name.json", EntityNameDuplicatesJsonGenerator.PublishNameJson);
        WriteFile("entity_name_wiki.json", stream => WikiEntityNameGenerator.PublishJson(stream, _context.ResourceManager, _context.Destination));
        WriteFile("entity_name_duplicates.json", EntityNameDuplicatesJsonGenerator.PublishDuplicatesJson);
        WriteFile("tag.json", TagJsonGenerator.PublishJson);

        PrototypeJsonGenerator.PublishAll(_context);

        ComponentJsonGenerator.PublishAll(_context);
    }

    private void WriteFile(string name, Action<Stream> write)
    {
        using var stream = _context.ResourceManager.UserData.OpenWrite(_context.Destination.WithName(name));
        write(stream);
    }
}
