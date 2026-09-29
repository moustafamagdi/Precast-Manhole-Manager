using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class OpeningAdoptionStorageService
    {
        private static readonly Guid SchemaGuid = new Guid("B3C9B6EE-8B57-4D9A-95FD-801C5C3D2B7B");

        public static void MarkAdoptedManual(Opening opening)
        {
            if (opening == null) return;

            Schema schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField("AdoptedManual"), true);
            entity.Set(schema.GetField("UpdatedUtc"), DateTime.UtcNow.ToString("O"));
            opening.SetEntity(entity);
        }

        public static bool IsAdoptedManual(Opening opening)
        {
            if (opening == null) return false;

            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return false;

            Entity entity = opening.GetEntity(schema);
            if (!entity.IsValid()) return false;

            Field field = schema.GetField("AdoptedManual");
            return field != null && entity.Get<bool>(field);
        }

        private static Schema GetOrCreateSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("HatcoPrecastManholeOpeningAdoption");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);

            builder.AddSimpleField("AdoptedManual", typeof(bool));
            builder.AddSimpleField("UpdatedUtc", typeof(string));

            return builder.Finish();
        }
    }
}
