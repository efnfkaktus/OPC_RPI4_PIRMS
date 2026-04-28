using opc_pri_plc;
using Supabase;
using Postgrest;
using Microsoft.Extensions.DependencyInjection;

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args
    });

    // Konfigurace z appsettings.json
    builder.Configuration.SetBasePath(AppContext.BaseDirectory);
    builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

    // Registrace konfigurací
    builder.Services.Configure<PlcConfig>(builder.Configuration.GetSection("PlcConfig"));

    // --- KLÍČOVÁ ZMĚNA PRO OVLÁDÁNÍ ---
    // Registrujeme OPC jako Singleton, aby k němu mohlo API i BackgroundService
    builder.Services.AddSingleton<OPC>();
    // Tímto řekneme aplikaci, že má OPC běžet i na pozadí
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OPC>());

    builder.Services.AddSingleton<DataProcessor>();
    builder.Services.AddControllers();

    // Supabase klient
    var url = builder.Configuration["Supabase:Url"] ?? throw new Exception("Chybí Supabase:Url v appsettings.json!");
    var key = builder.Configuration["Supabase:Key"] ?? throw new Exception("Chybí Supabase:Key v appsettings.json!");

    builder.Services.AddScoped<Supabase.Client>(_ =>
        new Supabase.Client(url, key, new SupabaseOptions { AutoConnectRealtime = true })
    );

    // CORS - aby mohl web volat API
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
    });

    var app = builder.Build();

    // Middleware pořadí je důležité
    app.UseCors();
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // --- API ENDPOINTY ---

    // Poskytnutí klíčů pro Frontend
    app.MapGet("/api/config", (IConfiguration config) =>
    {
        return Results.Ok(new
        {
            url = config["Supabase:Url"],
            key = config["Supabase:Key"]
        });
    });

    // Zápis do PLC (Ovládání tlačítky)
    app.MapPost("/api/write", async (WriteRequest request, OPC opcService) =>
    {
        Console.WriteLine($"Příjat požadavek na zápis: {request.TagName} = {request.Value}");

        bool success = await opcService.WriteBoolValueAsync(request.TagName, request.Value);

        if (success)
        {
            return Results.Ok(new { message = "Zápis byl úspěšný" });
        }
        else
        {
            return Results.BadRequest(new { message = "Zápis selhal. Je PLC připojeno?" });
        }
    });

    app.MapControllers();

    Console.WriteLine("🚀 Webový server a OPC Bridge startují...");
    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine("!!! KRITICKÝ PÁD PŘI STARTU !!!");
    Console.WriteLine(ex.Message);
    if (ex.InnerException != null) Console.WriteLine($"Vnitřní chyba: {ex.InnerException.Message}");

    Console.WriteLine("\nStiskni Enter pro ukončení...");
    Console.ReadLine();
}

// Definice požadavku pro zápis (může být i v samostatném souboru)
public record WriteRequest(string TagName, bool Value);