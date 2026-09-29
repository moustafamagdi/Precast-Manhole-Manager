using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class ManholeDataCarrierService
    {
        private static readonly Guid SchemaGuid = new Guid("02B393B2-3DD2-4A72-8A7C-45F1E4175A31");

        public static DirectShape CreateOrUpdate(Document doc, ManholeDataRecord data)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (data == null) throw new ArgumentNullException(nameof(data));

            DirectShape carrier = FindByFoundation(doc, data.FoundationUniqueId, data.FoundationId);
            if (carrier == null)
            {
                carrier = DirectShape.CreateElement(doc, new ElementId((int)BuiltInCategory.OST_GenericModel));
                carrier.ApplicationId = "HATCO.PrecastManholeManager";
                carrier.ApplicationDataId = data.Key;
                carrier.SetShape(new List<GeometryObject> { CreateMarkerSolid(data) });
            }
            else
            {
                carrier.SetShape(new List<GeometryObject> { CreateMarkerSolid(data) });
            }

            Parameter mark = carrier.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (mark != null && !mark.IsReadOnly)
                mark.Set(data.ManholeNumber ?? string.Empty);

            Parameter comments = carrier.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments != null && !comments.IsReadOnly)
            {
                comments.Set(
                    $"Precast Manhole | Clear {data.ClearW2W3Mm:0} x {data.ClearW1W4Mm:0} mm | " +
                    $"Foundation {data.FoundationId}");
            }

            WriteData(carrier, data);
            return carrier;
        }

        public static ManholeDataRecord ReadForFoundation(Document doc, string foundationUniqueId, int foundationId)
        {
            DirectShape carrier = FindByFoundation(doc, foundationUniqueId, foundationId);
            if (carrier == null) return null;

            return TryReadData(carrier, out ManholeDataRecord data) ? data : null;
        }

        public static List<ManholeDataRecord> ReadAll(Document doc)
        {
            var result = new List<ManholeDataRecord>();

            foreach (DirectShape ds in new FilteredElementCollector(doc)
                         .OfClass(typeof(DirectShape))
                         .Cast<DirectShape>())
            {
                if (ds.Category == null ||
                    ds.Category.Id.IntegerValue != (int)BuiltInCategory.OST_GenericModel)
                    continue;

                if (TryReadData(ds, out ManholeDataRecord data))
                    result.Add(data);
            }

            return result
                .OrderBy(x => x.ManholeNumber ?? string.Empty)
                .ThenBy(x => x.FoundationId)
                .ToList();
        }

        private static DirectShape FindByFoundation(Document doc, string foundationUniqueId, int foundationId)
        {
            foreach (DirectShape ds in new FilteredElementCollector(doc)
                         .OfClass(typeof(DirectShape))
                         .Cast<DirectShape>())
            {
                if (ds.Category == null ||
                    ds.Category.Id.IntegerValue != (int)BuiltInCategory.OST_GenericModel)
                    continue;

                if (!TryReadData(ds, out ManholeDataRecord data))
                    continue;

                if (!string.IsNullOrWhiteSpace(foundationUniqueId) &&
                    string.Equals(data.FoundationUniqueId, foundationUniqueId, StringComparison.Ordinal))
                    return ds;

                if (data.FoundationId == foundationId)
                    return ds;
            }

            return null;
        }

        private static Solid CreateMarkerSolid(ManholeDataRecord data)
        {
            double half = UnitUtil.MmToFt(10);
            double height = UnitUtil.MmToFt(20);

            double cx = UnitUtil.MmToFt(data.CenterXmm);
            double cy = UnitUtil.MmToFt(data.CenterYmm);
            double z = UnitUtil.MmToFt(data.BaseTopZmm) + UnitUtil.MmToFt(20);

            XYZ p1 = new XYZ(cx - half, cy - half, z);
            XYZ p2 = new XYZ(cx + half, cy - half, z);
            XYZ p3 = new XYZ(cx + half, cy + half, z);
            XYZ p4 = new XYZ(cx - half, cy + half, z);

            CurveLoop loop = new CurveLoop();
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p4));
            loop.Append(Line.CreateBound(p4, p1));

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop },
                XYZ.BasisZ,
                height);
        }

        private static Schema GetOrCreateSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("HatcoPrecastManholeDataCarrier");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);

            builder.AddSimpleField("ManholeNumber", typeof(string));
            builder.AddSimpleField("FoundationId", typeof(int));
            builder.AddSimpleField("FoundationUniqueId", typeof(string));
            builder.AddSimpleField("Wall1Id", typeof(int));
            builder.AddSimpleField("Wall2Id", typeof(int));
            builder.AddSimpleField("Wall3Id", typeof(int));
            builder.AddSimpleField("Wall4Id", typeof(int));

            AddLengthField(builder, "CenterXmm");
            AddLengthField(builder, "CenterYmm");
            AddLengthField(builder, "BaseTopZmm");
            AddLengthField(builder, "BaseThicknessMm");
            AddLengthField(builder, "ClearW1W4Mm");
            AddLengthField(builder, "ClearW2W3Mm");
            AddLengthField(builder, "OuterW1W4Mm");
            AddLengthField(builder, "OuterW2W3Mm");
            AddLengthField(builder, "WallHeightMm");

            builder.AddSimpleField("UpdatedUtc", typeof(string));

            return builder.Finish();
        }

        private static void AddLengthField(SchemaBuilder builder, string name)
        {
            FieldBuilder field = builder.AddSimpleField(name, typeof(double));
            field.SetSpec(SpecTypeId.Length);
        }

        private static void WriteData(DirectShape carrier, ManholeDataRecord data)
        {
            Schema schema = GetOrCreateSchema();
            var entity = new Entity(schema);

            entity.Set(schema.GetField("ManholeNumber"), data.ManholeNumber ?? string.Empty);
            entity.Set(schema.GetField("FoundationId"), data.FoundationId);
            entity.Set(schema.GetField("FoundationUniqueId"), data.FoundationUniqueId ?? string.Empty);
            entity.Set(schema.GetField("Wall1Id"), data.Wall1Id);
            entity.Set(schema.GetField("Wall2Id"), data.Wall2Id);
            entity.Set(schema.GetField("Wall3Id"), data.Wall3Id);
            entity.Set(schema.GetField("Wall4Id"), data.Wall4Id);

            SetMm(entity, schema, "CenterXmm", data.CenterXmm);
            SetMm(entity, schema, "CenterYmm", data.CenterYmm);
            SetMm(entity, schema, "BaseTopZmm", data.BaseTopZmm);
            SetMm(entity, schema, "BaseThicknessMm", data.BaseThicknessMm);
            SetMm(entity, schema, "ClearW1W4Mm", data.ClearW1W4Mm);
            SetMm(entity, schema, "ClearW2W3Mm", data.ClearW2W3Mm);
            SetMm(entity, schema, "OuterW1W4Mm", data.OuterW1W4Mm);
            SetMm(entity, schema, "OuterW2W3Mm", data.OuterW2W3Mm);
            SetMm(entity, schema, "WallHeightMm", data.WallHeightMm);

            entity.Set(schema.GetField("UpdatedUtc"), DateTime.UtcNow.ToString("O"));
            carrier.SetEntity(entity);
        }

        private static bool TryReadData(DirectShape carrier, out ManholeDataRecord data)
        {
            data = null;
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return false;

            Entity entity = carrier.GetEntity(schema);
            if (!entity.IsValid()) return false;

            data = new ManholeDataRecord
            {
                ManholeNumber = entity.Get<string>(schema.GetField("ManholeNumber")),
                FoundationId = entity.Get<int>(schema.GetField("FoundationId")),
                FoundationUniqueId = entity.Get<string>(schema.GetField("FoundationUniqueId")),
                Wall1Id = entity.Get<int>(schema.GetField("Wall1Id")),
                Wall2Id = entity.Get<int>(schema.GetField("Wall2Id")),
                Wall3Id = entity.Get<int>(schema.GetField("Wall3Id")),
                Wall4Id = entity.Get<int>(schema.GetField("Wall4Id")),
                CenterXmm = GetMm(entity, schema, "CenterXmm"),
                CenterYmm = GetMm(entity, schema, "CenterYmm"),
                BaseTopZmm = GetMm(entity, schema, "BaseTopZmm"),
                BaseThicknessMm = GetMm(entity, schema, "BaseThicknessMm"),
                ClearW1W4Mm = GetMm(entity, schema, "ClearW1W4Mm"),
                ClearW2W3Mm = GetMm(entity, schema, "ClearW2W3Mm"),
                OuterW1W4Mm = GetMm(entity, schema, "OuterW1W4Mm"),
                OuterW2W3Mm = GetMm(entity, schema, "OuterW2W3Mm"),
                WallHeightMm = GetMm(entity, schema, "WallHeightMm")
            };

            return true;
        }

        private static void SetMm(Entity entity, Schema schema, string fieldName, double value)
        {
            entity.Set(schema.GetField(fieldName), value, UnitTypeId.Millimeters);
        }

        private static double GetMm(Entity entity, Schema schema, string fieldName)
        {
            return entity.Get<double>(schema.GetField(fieldName), UnitTypeId.Millimeters);
        }
    }
}
