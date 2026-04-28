namespace opc_pri_plc;

using Opc.Ua;
using Opc.Ua.Client;

public class DataProcessor
{
    public PlcDataModel ProcessToModel(MonitoredItem item, MonitoredItemNotification notification)
    {
        var dataValue = notification.Value;
        string typeName = dataValue.WrappedValue.TypeInfo.BuiltInType.ToString();
        string nodeId = item.StartNodeId.ToString();
        string cleanName = nodeId.Split('.').Last().Replace("\"", "");

        // Logika převodu hodnoty (stejná jako v tvém Pythonu)
        object finalValue = dataValue.Value switch
        {
            bool b => b ? 1.0 : 0.0,
            short s => Convert.ToDouble(s),
            int i => Convert.ToDouble(i),
            float f => Convert.ToDouble(f),
            double d => d,
            _ => dataValue.Value?.ToString() ?? "null"
        };

        return new PlcDataModel
        {
            TagName = cleanName,
            DataType = typeName,
            Value = finalValue,
            ReceivedAt = DateTime.UtcNow
        };
    }
}