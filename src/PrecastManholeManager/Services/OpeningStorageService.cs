using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class OpeningStorageService
    {
        private static readonly Guid SchemaGuid = new Guid("5B3286B5-3707-4D43-A6AF-AFC2A44871A4");

        private static Schema GetOrCreateSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("HatcoPrecastManholeOpening");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);

            builder.AddSimpleField("SourceKey", typeof(string));
            builder.AddSimpleField("LinkInstanceId", typeof(int));
            builder.AddSimpleField("LinkedElementId", typeof(int));
            builder.AddSimpleField("LinkedUniqueId", typeof(string));
            builder.AddSimpleField("LinkName", typeof(string));
            builder.AddSimpleField("HostWallId", typeof(int));
            builder.AddSimpleField("WallNumber", typeof(int));
            builder.AddSimpleField("SourceShape", typeof(string));

            AddLengthField(builder, "CutWidthMm");
            AddLengthField(builder, "CutHeightMm");
            AddLengthField(builder, "ClearanceMm");
            AddLengthField(builder, "Xmm");
            AddLengthField(builder, "Ymm");
            AddLengthField(builder, "Zmm");

            builder.AddSimpleField("UpdatedUtc", typeof(string));

            return builder.Finish();
        }

        private static void AddLengthField(SchemaBuilder builder, string name)
        {
            FieldBuilder field = builder.AddSimpleField(name, typeof(double));
            field.SetSpec(SpecTypeId.Length);
        }

        public static void Write(Opening opening, PenetrationRecord record)
        {
            Schema schema = GetOrCreateSchema();
            var entity = new Entity(schema);

            entity.Set(schema.GetField("SourceKey"), record.SourceKey);
            entity.Set(schema.GetField("LinkInstanceId"), record.LinkInstanceId);
            entity.Set(schema.GetField("LinkedElementId"), record.LinkedElementId);
            entity.Set(schema.GetField("LinkedUniqueId"), record.LinkedUniqueId ?? string.Empty);
            entity.Set(schema.GetField("LinkName"), record.LinkName ?? string.Empty);
            entity.Set(schema.GetField("HostWallId"), record.HostWallId);
            entity.Set(schema.GetField("WallNumber"), record.WallNumber);
            entity.Set(schema.GetField("SourceShape"), record.Shape ?? string.Empty);

            SetMillimeters(entity, schema, "CutWidthMm", record.CutWidthMm);
            SetMillimeters(entity, schema, "CutHeightMm", record.CutHeightMm);
            SetMillimeters(entity, schema, "ClearanceMm", record.ClearanceMm);
            SetMillimeters(entity, schema, "Xmm", record.Xmm);
            SetMillimeters(entity, schema, "Ymm", record.Ymm);
            SetMillimeters(entity, schema, "Zmm", record.Zmm);

            entity.Set(schema.GetField("UpdatedUtc"), DateTime.UtcNow.ToString("O"));

            opening.SetEntity(entity);
        }

        public static void WriteAdoptedManual(Opening opening, PenetrationRecord record)
        {
            Schema schema = GetOrCreateSchema();
            var entity = new Entity(schema);

            entity.Set(schema.GetField("SourceKey"), record.SourceKey);
            entity.Set(schema.GetField("LinkInstanceId"), record.LinkInstanceId);
            entity.Set(schema.GetField("LinkedElementId"), record.LinkedElementId);
            entity.Set(schema.GetField("LinkedUniqueId"), record.LinkedUniqueId ?? string.Empty);
            entity.Set(schema.GetField("LinkName"), record.LinkName ?? string.Empty);
            entity.Set(schema.GetField("HostWallId"), record.HostWallId);
            entity.Set(schema.GetField("WallNumber"), record.WallNumber);
            entity.Set(schema.GetField("SourceShape"), record.Shape ?? string.Empty);

            SetMillimeters(entity, schema, "CutWidthMm", record.ExistingOpeningWidthMm);
            SetMillimeters(entity, schema, "CutHeightMm", record.ExistingOpeningHeightMm);
            SetMillimeters(entity, schema, "ClearanceMm", record.ClearanceMm);
            SetMillimeters(entity, schema, "Xmm", record.Xmm);
            SetMillimeters(entity, schema, "Ymm", record.Ymm);
            SetMillimeters(entity, schema, "Zmm", record.Zmm);

            entity.Set(schema.GetField("UpdatedUtc"), DateTime.UtcNow.ToString("O"));
            opening.SetEntity(entity);
        }

        public static bool TryRead(Opening opening, out ManagedOpeningData data)
        {
            data = null;
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return false;

            Entity entity = opening.GetEntity(schema);
            if (!entity.IsValid()) return false;

            data = new ManagedOpeningData
            {
                OpeningId = opening.Id.IntegerValue,
                SourceKey = entity.Get<string>(schema.GetField("SourceKey")),
                HostWallId = entity.Get<int>(schema.GetField("HostWallId")),
                WallNumber = entity.Get<int>(schema.GetField("WallNumber")),
                AdoptedManual = OpeningAdoptionStorageService.IsAdoptedManual(opening),
                CutWidthMm = GetMillimeters(entity, schema, "CutWidthMm"),
                CutHeightMm = GetMillimeters(entity, schema, "CutHeightMm"),
                Xmm = GetMillimeters(entity, schema, "Xmm"),
                Ymm = GetMillimeters(entity, schema, "Ymm"),
                Zmm = GetMillimeters(entity, schema, "Zmm")
            };

            return true;
        }

        private static void SetMillimeters(Entity entity, Schema schema, string fieldName, double valueMm)
        {
            entity.Set(schema.GetField(fieldName), valueMm, UnitTypeId.Millimeters);
        }

        private static double GetMillimeters(Entity entity, Schema schema, string fieldName)
        {
            return entity.Get<double>(schema.GetField(fieldName), UnitTypeId.Millimeters);
        }
    }

    internal sealed class ManagedOpeningData
    {
        public int OpeningId { get; set; }
        public string SourceKey { get; set; }
        public int HostWallId { get; set; }
        public int WallNumber { get; set; }
        public bool AdoptedManual { get; set; }
        public double CutWidthMm { get; set; }
        public double CutHeightMm { get; set; }
        public double Xmm { get; set; }
        public double Ymm { get; set; }
        public double Zmm { get; set; }
    }
}
