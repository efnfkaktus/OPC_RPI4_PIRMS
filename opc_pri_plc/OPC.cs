namespace opc_pri_plc;

using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;

public class OPC : BackgroundService
{
    private readonly DataProcessor _processor;
    private readonly ILogger<OPC> _logger;
    private readonly PlcConfig _config;
    private readonly IServiceScopeFactory _scopeFactory;

    private Session? _session;

    public OPC(DataProcessor processor, ILogger<OPC> logger, IOptions<PlcConfig> config, IServiceScopeFactory scopeFactory)
    {
        _processor = processor;
        _logger = logger;
        _config = config.Value;
        _scopeFactory = scopeFactory;
    }


    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 1. Konfigurace aplikace s fixními cestami pro Windows/Linux
                var baseDir = AppContext.BaseDirectory;
                var config = new ApplicationConfiguration()
                {
                    ApplicationName = "OPC_UA_Bridge",
                    ApplicationType = ApplicationType.Client,

                    ClientConfiguration = new ClientConfiguration
                    {
                        DefaultSessionTimeout = 60000
                    },
                    SecurityConfiguration = new SecurityConfiguration
                    {
                        ApplicationCertificate = new CertificateIdentifier
                        {
                            StoreType = "Directory",
                            StorePath = "./pki/own"
                        },
                        TrustedIssuerCertificates = new CertificateTrustList
                        {
                            StoreType = "Directory",
                            StorePath = "./pki/trusted" // TATO ŘÁDKA CHYBÍ
                        },
                        TrustedPeerCertificates = new CertificateTrustList
                        {
                            StoreType = "Directory",
                            StorePath = "./pki/trusted" // TATO ŘÁDKA CHYBÍ
                        },
                        RejectedCertificateStore = new CertificateTrustList
                        {
                            StoreType = "Directory",
                            StorePath = "./pki/rejected"
                        },
                        AutoAcceptUntrustedCertificates = true // Pro testování užitečné
                    },
                    TransportQuotas = new TransportQuotas { OperationTimeout = 15000 }
                };

                await config.Validate(ApplicationType.Client);

                // 2. Výběr endpointu
                string endpointUrl = _config.Url;
                var selectedEndpoint = await CoreClientUtils.SelectEndpointAsync(
                    application: config,
                    discoveryUrl: endpointUrl,
                    useSecurity: false,
                    telemetry: null!,
                    ct: ct
                );

                if (selectedEndpoint == null)
                {
                    _logger.LogError("Nepodařilo se najít endpoint na {Url}. Zkontrolujte spojení.", endpointUrl);
                    await Task.Delay(5000, ct);
                    continue;
                }

                var endpointConfiguration = EndpointConfiguration.Create(config);
                var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);

                // 3. Vytvoření Session
                _logger.LogInformation("Pripojování k PLC na {Url}...", endpointUrl);
                using var session = await Session.Create(config, endpoint, false, "BridgeSession", 60000, null, null);
                _logger.LogInformation("Pripojeno k PLC.");

                // 4. Vytvoření a REGISTRACE Subscription
                var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = 100 };

                // KLÍČOVÝ ŘÁDEK: Propojení odběru s relací
                session.AddSubscription(subscription);

                var nodesToBrowse = new BrowseDescriptionCollection {
                    new BrowseDescription {
                        NodeId = NodeId.Parse(_config.RootNode),
                        BrowseDirection = BrowseDirection.Forward,
                        ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                        IncludeSubtypes = true,
                        NodeClassMask = (uint)NodeClass.Variable,
                        ResultMask = (uint)BrowseResultMask.All
                    }
                };

                var browseResponse = await session.BrowseAsync(null, null, 0, nodesToBrowse, ct);

                if (browseResponse.Results != null && browseResponse.Results.Count > 0)
                {
                    foreach (var reference in browseResponse.Results[0].References)
                    {
                        var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);

                        var item = new MonitoredItem(subscription.DefaultItem)
                        {
                            DisplayName = reference.BrowseName.Name,
                            StartNodeId = nodeId,
                            SamplingInterval = 250
                        };

                        // Event handler pro změnu dat
                        item.Notification += (sender, e) =>
                        {
                            if (e.NotificationValue is MonitoredItemNotification notification)
                            {
                                var resultPayload = _processor.ProcessToModel(item, notification);
                                _logger.LogInformation("📡 {Tag} -> {Val}", resultPayload.TagName, resultPayload.Value);

                                // Odeslání do Supabase (běží na pozadí, aby nebrzdilo OPC UA stack)
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        using var scope = _scopeFactory.CreateScope();
                                        var client = scope.ServiceProvider.GetRequiredService<Supabase.Client>();
                                        await client.From<PlcDataModel>().Insert(resultPayload);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError("❌ DB Error: {Msg}", ex.Message);
                                    }
                                }, ct);
                            }
                        };

                        subscription.AddItem(item);
                    }
                }


                // 5. Aktivace odběru na serveru
                await subscription.CreateAsync(ct);
                _logger.LogInformation("🚀 Bridge monitoruje {Count} tagů.", subscription.MonitoredItemCount);

                // 6. Keep-alive smyčka
                while (!ct.IsCancellationRequested && session.Connected)
                {
                    await session.ReadValueAsync(Variables.Server_ServerStatus_CurrentTime, ct);
                    await Task.Delay(10000, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical("💥 KRITICKÁ CHYBA: {Message}", ex.Message);
                await Task.Delay(5000, ct); // Krátká pauza před restartem smyčky
            }


        }
    }

    public async Task<bool> WriteBoolValueAsync(string tagName, bool value)
    {
        if (_session == null || !_session.Connected)
        {
            _logger.LogWarning("Zápis selhal: PLC není připojeno.");
            return false;
        }

        try
        {
            // Sestavení NodeId. POZOR: ns=2 a oddělovač "." musí odpovídat vašemu PLC!
            // Pokud je RootNode např. "ns=2;s=Data", pak výsledné NodeId bude "ns=2;s=Data.MojePromenna"
            NodeId node = new NodeId($"{_config.RootNode}.{tagName}");

            WriteValue valueToWrite = new WriteValue
            {
                NodeId = node,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(value))
            };

            WriteValueCollection valuesToWrite = new WriteValueCollection { valueToWrite };

            // Synchronní volání Write s diagnostikou
            _session.Write(
                null,
                valuesToWrite,
                out StatusCodeCollection results,
                out DiagnosticInfoCollection diag);

            bool isGood = StatusCode.IsGood(results[0]);

            if (isGood)
                _logger.LogInformation("✅ Úspěšný zápis do PLC: {Tag} = {Val}", tagName, value);
            else
                _logger.LogError("❌ Chyba zápisu do PLC: {Status}", results[0]);

            return isGood;
        }
        catch (Exception ex)
        {
            _logger.LogError("💥 Výjimka při zápisu do PLC: {Msg}", ex.Message);
            return false;
        }
    }


}