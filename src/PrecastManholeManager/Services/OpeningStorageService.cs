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
            builder.AddSimpleField("CutWidthMm", typeof(double));
            builder.AddSimpleField("CutHeightMm", typeof(double));
            builder.AddSimpleField("ClearanceMm", typeof(double));
            builder.AddSimpleField("Xmm", typeof(double));
            builder.AddSimpleField("Ymm", typeof(double));
            builder.AddSimpleField("Zmm", typeof(double));
            builder.AddSimpleField("UpdatedUtc", typeof(string));

            return builder.Finish();
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
            entity.Set(schema.GetField("CutWidthMm"), record.CutWidthMm);
            entity.Set(schema.GetField("CutHeightMm"), record.CutHeightMm);
            entity.Set(schema.GetField("ClearanceMm"), record.ClearanceMm);
            entity.Set(schema.GetField("Xmm"), record.Xmm);
            entity.Set(schema.GetField("Ymm"), record.Ymm);
            entity.Set(schema.GetField("Zmm"), record.Zmm);
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
                CutWidthMm = entity.Get<double>(schema.GetField("CutWidthMm")),
                CutHeightMm = entity.Get<double>(schema.GetField("CutHeightMm")),
                Xmm = entity.Get<double>(schema.GetField("Xmm")),
                Ymm = entity.Get<double>(schema.GetField("Ymm")),
                Zmm = entity.Get<double>(schema.GetField("Zmm"))
            };

            return true;
        }
    }

    internal sealed class ManagedOpeningData
    {
        public int OpeningId { get; set; }
        public string SourceKey { get; set; }
        public int HostWallId { get; set; }
        public double CutWidthMm { get; set; }
        public double CutHeightMm { get; set; }
        public double Xmm { get; set; }
        public double Ymm { get; set; }
        public double Zmm { get; set; }
    }
}
