using Microsoft.AspNetCore.Mvc;
using Supabase; // Pro přístup k Supabase.Client
using Postgrest; // Pro Constants.Ordering

namespace opc_pri_plc;

[ApiController]
[Route("api/[controller]")]
public class PlcController : ControllerBase
{
    private readonly Supabase.Client _client;

    public PlcController(Supabase.Client client)
    {
        _client = client;
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory()
    {
        // Vrátí posledních 100 záznamů pro graf
        var response = await _client.From<PlcDataModel>()
                                   .Order("received_at", Constants.Ordering.Descending)
                                   .Limit(100)
                                   .Get();
        return Ok(response.Models);
    }
}