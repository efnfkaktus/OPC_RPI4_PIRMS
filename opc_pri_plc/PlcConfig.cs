namespace opc_pri_plc;

public class PlcConfig
{
    public string Url { get; set; } = string.Empty;
    public string RootNode { get; set; } = string.Empty;
}

public class PlcPayload
{
    public string tag_name { get; set; } = string.Empty;
    public string data_type { get; set; } = string.Empty;
    public object? value { get; set; }
    public string received_at { get; set; } = string.Empty;
}