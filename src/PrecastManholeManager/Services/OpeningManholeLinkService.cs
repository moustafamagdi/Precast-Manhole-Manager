using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class OpeningManholeLinkService
    {
        private static readonly Guid SchemaGuid = new Guid("6C825D42-B51C-4C0D-9D7F-7B2E8FCB2A11");

        public static int LinkManagedOpenings(
            Document doc,
            string manholeNumber,
            int foundationId,
            IEnumerable<int> wallIds)
        {
            HashSet<int> walls = new HashSet<int>(wallIds ?? Enumerable.Empty<int>());
            var items = new List<LinkCandidate>();

            foreach (Opening opening in new FilteredElementCollector(doc)
                         .OfClass(typeof(Opening))
                         .Cast<Opening>())
            {
                ManagedOpeningData managed;
                if (!OpeningStorageService.TryRead(opening, out managed))
                    continue;

                if (!walls.Contains(managed.HostWallId))
                    continue;

                items.Add(new LinkCandidate
                {
                    Opening = opening,
                    Managed = managed
                });
            }

            int linked = 0;

            foreach (IGrouping<int, LinkCandidate> wallGroup in items
                         .GroupBy(x => x.Managed.HostWallId))
            {
                int wallNumber = ResolveWallNumber(doc, wallGroup.Key, manholeNumber);

                int index = 1;
                foreach (LinkCandidate item in wallGroup
                             .OrderBy(x => x.Managed.Zmm)
                             .ThenBy(x => x.Managed.Xmm)
                             .ThenBy(x => x.Managed.Ymm)
                             .ThenBy(x => x.Opening.Id.IntegerValue))
                {
                    string openingNumber = "O" + index.ToString("00");
                    string code =
                        (manholeNumber ?? string.Empty) +
                        "-W" + wallNumber +
                        "-" + openingNumber;

                    WriteLink(
                        item.Opening,
                        manholeNumber,
                        foundationId,
                        wallNumber,
                        openingNumber,
                        code);

                    linked++;
                    index++;
                }
            }

            return linked;
        }

        private static int ResolveWallNumber(Document doc, int wallId, string manholeNumber)
        {
            // Wall number is already persisted in the managed opening source schema.
            foreach (Opening opening in new FilteredElementCollector(doc)
                         .OfClass(typeof(Opening))
                         .Cast<Opening>())
            {
                ManagedOpeningData data;
                if (!OpeningStorageService.TryRead(opening, out data))
                    continue;

                if (data.HostWallId != wallId)
                    continue;

                return ReadManagedWallNumber(opening);
            }

            return 0;
        }

        private static int ReadManagedWallNumber(Opening opening)
        {
            Guid openingSchemaGuid = new Guid("5B3286B5-3707-4D43-A6AF-AFC2A44871A4");
            Schema schema = Schema.Lookup(openingSchemaGuid);
            if (schema == null) return 0;

            Entity entity = opening.GetEntity(schema);
            if (!entity.IsValid()) return 0;

            Field field = schema.GetField("WallNumber");
            return field == null ? 0 : entity.Get<int>(field);
        }

        private static Schema GetOrCreateSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("HatcoPrecastManholeOpeningLink");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);

            builder.AddSimpleField("ManholeNumber", typeof(string));
            builder.AddSimpleField("FoundationId", typeof(int));
            builder.AddSimpleField("WallNumber", typeof(int));
            builder.AddSimpleField("OpeningNumber", typeof(string));
            builder.AddSimpleField("OpeningCode", typeof(string));
            builder.AddSimpleField("UpdatedUtc", typeof(string));

            return builder.Finish();
        }

        private static void WriteLink(
            Opening opening,
            string manholeNumber,
            int foundationId,
            int wallNumber,
            string openingNumber,
            string openingCode)
        {
            Schema schema = GetOrCreateSchema();
            var entity = new Entity(schema);

            entity.Set(schema.GetField("ManholeNumber"), manholeNumber ?? string.Empty);
            entity.Set(schema.GetField("FoundationId"), foundationId);
            entity.Set(schema.GetField("WallNumber"), wallNumber);
            entity.Set(schema.GetField("OpeningNumber"), openingNumber ?? string.Empty);
            entity.Set(schema.GetField("OpeningCode"), openingCode ?? string.Empty);
            entity.Set(schema.GetField("UpdatedUtc"), DateTime.UtcNow.ToString("O"));

            opening.SetEntity(entity);
        }

        private sealed class LinkCandidate
        {
            public Opening Opening { get; set; }
            public ManagedOpeningData Managed { get; set; }
        }
    }
}
