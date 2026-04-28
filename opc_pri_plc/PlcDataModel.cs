using Postgrest.Attributes;
using Postgrest.Models;

namespace opc_pri_plc;

[Table("plc_data")]
public class PlcDataModel : BaseModel
{
    [PrimaryKey("id", false)] // "id" je název sloupce v DB, false znamená, že ho negeneruje klient
    public int Id { get; set; }
    
    [Column("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [Column("data_type")]
    public string DataType { get; set; } = string.Empty;

    [Column("value")]
    public object? Value { get; set; }

    [Column("received_at")]
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}