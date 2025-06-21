using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using RobTeach.Models;
using IxMilia.Dxf; // For DxfPoint and DxfVector

namespace RobTeach.Services
{
    public class TrajectoryJsonConverter : JsonConverter<Trajectory>
    {
        public override Trajectory Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected StartObject token");
            }

            using (JsonDocument jsonDocument = JsonDocument.ParseValue(ref reader))
            {
                JsonElement root = jsonDocument.RootElement;
                var trajectory = new Trajectory();

                // Helper function to get string property
                string GetStringProperty(JsonElement element, string propertyName)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
                           ? property.GetString() ?? string.Empty // Ensure null is converted to empty string if GetString() returns null
                           : string.Empty;
                }

                // Helper function for boolean property
                bool GetBooleanProperty(JsonElement element, string propertyName, bool defaultValue = false)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property) && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
                           ? property.GetBoolean()
                           : defaultValue;
                }

                // Helper function for int property
                int GetIntProperty(JsonElement element, string propertyName, int defaultValue = 0)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.Number
                           ? property.GetInt32()
                           : defaultValue;
                }

                // Helper function for double property
                double GetDoubleProperty(JsonElement element, string propertyName, double defaultValue = 0.0)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.Number
                           ? property.GetDouble()
                           : defaultValue;
                }

                // Helper function for DxfPoint
                DxfPoint GetDxfPointProperty(JsonElement element, string propertyName, JsonSerializerOptions options)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property)
                           ? JsonSerializer.Deserialize<DxfPoint>(property.GetRawText(), options)
                           : DxfPoint.Origin;
                }

                // Helper function for DxfVector
                DxfVector GetDxfVectorProperty(JsonElement element, string propertyName, JsonSerializerOptions options)
                {
                    return element.TryGetProperty(propertyName, out JsonElement property)
                           ? JsonSerializer.Deserialize<DxfVector>(property.GetRawText(), options)
                           // Note: DxfVector is a struct, so ?? DxfVector.Zero might also be redundant if Deserialize never returns null for structs.
                           // However, for consistency with potential future changes or if it were a class, keeping it for DxfVector for now.
                           // Or, if DxfVectorJsonConverter handles missing properties by returning default, this ?? is also not strictly needed.
                           // For this change, only DxfPoint was specified.
                           // The above comment is now outdated as we are removing the ?? DxfVector.Zero as per instruction for DxfVector as well.
                           : DxfVector.Zero;
                }

                // Deserialize common properties
                trajectory.OriginalEntityHandle = GetStringProperty(root, "OriginalEntityHandle");
                trajectory.EntityType = GetStringProperty(root, "EntityType");
                trajectory.PrimitiveType = GetStringProperty(root, "PrimitiveType");

                trajectory.IsReversed = GetBooleanProperty(root, "IsReversed");
                trajectory.NozzleNumber = GetIntProperty(root, "NozzleNumber");

                trajectory.UpperNozzleEnabled = GetBooleanProperty(root, "UpperNozzleEnabled");
                trajectory.UpperNozzleGasOn = GetBooleanProperty(root, "UpperNozzleGasOn");
                trajectory.UpperNozzleLiquidOn = GetBooleanProperty(root, "UpperNozzleLiquidOn");
                trajectory.LowerNozzleEnabled = GetBooleanProperty(root, "LowerNozzleEnabled");
                trajectory.LowerNozzleGasOn = GetBooleanProperty(root, "LowerNozzleGasOn");
                trajectory.LowerNozzleLiquidOn = GetBooleanProperty(root, "LowerNozzleLiquidOn");

                // Conditionally deserialize geometric properties
                if (trajectory.PrimitiveType == "Line")
                {
                    trajectory.LineStartPoint = GetDxfPointProperty(root, "LineStartPoint", options);
                    trajectory.LineEndPoint = GetDxfPointProperty(root, "LineEndPoint", options);
                }
                else if (trajectory.PrimitiveType == "Arc")
                {
                    trajectory.ArcCenter = GetDxfPointProperty(root, "ArcCenter", options);
                    trajectory.ArcRadius = GetDoubleProperty(root, "ArcRadius");
                    trajectory.ArcStartAngle = GetDoubleProperty(root, "ArcStartAngle");
                    trajectory.ArcEndAngle = GetDoubleProperty(root, "ArcEndAngle");
                    trajectory.ArcNormal = GetDxfVectorProperty(root, "ArcNormal", options);
                }
                else if (trajectory.PrimitiveType == "Circle")
                {
                    trajectory.CircleCenter = GetDxfPointProperty(root, "CircleCenter", options);
                    trajectory.CircleRadius = GetDoubleProperty(root, "CircleRadius");
                    trajectory.CircleNormal = GetDxfVectorProperty(root, "CircleNormal", options);
                }
                return trajectory;
            }
        }

        public override void Write(Utf8JsonWriter writer, Trajectory value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            // Common string properties
            if (!string.IsNullOrEmpty(value.OriginalEntityHandle))
            {
                writer.WriteString("OriginalEntityHandle", value.OriginalEntityHandle);
            }
            if (!string.IsNullOrEmpty(value.EntityType))
            {
                writer.WriteString("EntityType", value.EntityType);
            }
            if (!string.IsNullOrEmpty(value.PrimitiveType))
            {
                writer.WriteString("PrimitiveType", value.PrimitiveType);
            }

            // Boolean properties
            writer.WriteBoolean("IsReversed", value.IsReversed);
            writer.WriteNumber("NozzleNumber", value.NozzleNumber); // Assuming NozzleNumber is int as per typical usage

            // Nozzle control boolean properties
            writer.WriteBoolean("UpperNozzleEnabled", value.UpperNozzleEnabled);
            writer.WriteBoolean("UpperNozzleGasOn", value.UpperNozzleGasOn);
            writer.WriteBoolean("UpperNozzleLiquidOn", value.UpperNozzleLiquidOn);
            writer.WriteBoolean("LowerNozzleEnabled", value.LowerNozzleEnabled);
            writer.WriteBoolean("LowerNozzleGasOn", value.LowerNozzleGasOn);
            writer.WriteBoolean("LowerNozzleLiquidOn", value.LowerNozzleLiquidOn);

            // Geometric properties based on PrimitiveType
            switch (value.PrimitiveType)
            {
                case "Line":
                    writer.WritePropertyName("LineStartPoint");
                    JsonSerializer.Serialize(writer, value.LineStartPoint, options);
                    writer.WritePropertyName("LineEndPoint");
                    JsonSerializer.Serialize(writer, value.LineEndPoint, options);
                    break;
                case "Arc":
                    writer.WritePropertyName("ArcCenter");
                    JsonSerializer.Serialize(writer, value.ArcCenter, options);
                    writer.WriteNumber("ArcRadius", value.ArcRadius);
                    writer.WriteNumber("ArcStartAngle", value.ArcStartAngle);
                    writer.WriteNumber("ArcEndAngle", value.ArcEndAngle);
                    writer.WritePropertyName("ArcNormal");
                    JsonSerializer.Serialize(writer, value.ArcNormal, options);
                    break;
                case "Circle":
                    writer.WritePropertyName("CircleCenter");
                    JsonSerializer.Serialize(writer, value.CircleCenter, options);
                    writer.WriteNumber("CircleRadius", value.CircleRadius);
                    writer.WritePropertyName("CircleNormal");
                    JsonSerializer.Serialize(writer, value.CircleNormal, options);
                    break;
            }

            writer.WriteEndObject();
        }
    }
}
