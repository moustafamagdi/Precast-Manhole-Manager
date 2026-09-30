using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Hatco.PrecastManholeManager.Services
{
    // Tool-only ID stored on the host foundation instance, not
    // the internal Revit ElementId. It is committed atomically together
    // with the first physical production openings and their sheet.
    internal static class ManholeIdentityStore
    {
        private static readonly Guid IdentitySchemaId =
            new Guid("8F7B1A3C-43CF-4A73-8EAD-21F550CD6836");

        public static string Read(Element foundation)
        {
            Schema schema = Schema.Lookup(IdentitySchemaId);
            if (schema == null || foundation == null) return null;
            Entity entity = foundation.GetEntity(schema);
            if (!entity.IsValid()) return null;
            Field field = schema.GetField("ManholeName");
            return field == null ? null : entity.Get<string>(field);
        }

        public static void Write(Element foundation, string name)
        {
            if (foundation == null ||
                string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException(
                    "A real manhole designation is required.");
            name = name.Trim();
            if (name.Length > 80)
                throw new InvalidOperationException(
                    "Manhole designation must be at most 80 characters.");

            Schema schema = Schema.Lookup(IdentitySchemaId);
            if (schema == null)
            {
                var builder = new SchemaBuilder(IdentitySchemaId);
                builder.SetSchemaName("HatcoManholeDesignation");
                builder.SetReadAccessLevel(AccessLevel.Public);
                builder.SetWriteAccessLevel(AccessLevel.Public);
                builder.AddSimpleField("ManholeName", typeof(string));
                schema = builder.Finish();
            }
            var data = new Entity(schema);
            data.Set(schema.GetField("ManholeName"), name);
            foundation.SetEntity(data);
        }
    }
}
