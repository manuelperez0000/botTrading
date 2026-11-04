#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Timers;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using NinjaTrader.Data;
using NinjaTrader.Indicator;
using Newtonsoft.Json.Linq; // NinjaTrader 8 incluye Newtonsoft.Json de serie
#endregion

// Namespace de tu estrategia
namespace NinjaTrader.NinjaScript.Strategies
{
    public class NodeApiTradingBot : Strategy
    {
        private System.Timers.Timer apiTimer;
        private int targetContracts = 0;
        private bool pendingTrade = false;
        private bool isLongDirection = true;
        private Random random;
        private readonly object lockObject = new object();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Bot que consulta una API de Node.js cada 1 minuto y opera aleatoriamente Long/Short.";
                Name = "NodeApiTradingBot";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                SyncAccountPosition = true;
                IsInstantiatedOnEachOptimizationIteration = true;
            }
            else if (State == State.Configure)
            {
                random = new Random();
            }
            else if (State == State.DataLoaded)
            {
                // Configurar un Timer para consultar la API cada 60,000 ms (1 minuto exacto)
                apiTimer = new System.Timers.Timer(60000);
                apiTimer.Elapsed += OnApiTimerElapsed;
                apiTimer.AutoReset = true;
                apiTimer.Enabled = true;
            }
            else if (State == State.Terminated)
            {
                if (apiTimer != null)
                {
                    apiTimer.Stop();
                    apiTimer.Dispose();
                }
            }
        }

        private void OnApiTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                string url = "http://localhost:3000/api";
                
                // WebClient síncrono pero seguro al ejecutarse dentro del hilo del Timer (fuera del hilo principal de NT8)
                using (WebClient client = new WebClient())
                {
                    string jsonResponse = client.DownloadString(url);
                    
                    // Parsear la respuesta JSON de tu API: { "contracts": X }
                    JObject data = JObject.Parse(jsonResponse);
                    int contracts = data["contracts"]?.Value<int>() ?? 0;

                    if (contracts > 0)
                    {
                        lock (lockObject)
                        {
                            targetContracts = contracts;
                            // Decidir aleatoriamente la dirección: true = Long, false = Short
                            isLongDirection = random.Next(2) == 0;
                            pendingTrade = true;
                        }
                        Print(string.Format("[API] Éxito. Contratos: {0} | Dirección: {1}", contracts, (isLongDirection ? "LONG" : "SHORT")));
                    }
                    else
                    {
                        Print("[API] Se recibieron 0 contratos. No se operará.");
                    }
                }
            }
            catch (Exception ex)
            {
                Print("[API Error] No se pudo conectar a localhost:3000 -> " + ex.Message);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;

            bool execute = false;
            int qty = 0;
            bool longDir = true;

            // Sincronizar el acceso a las variables compartidas con el Timer
            lock (lockObject)
            {
                if (pendingTrade)
                {
                    execute = true;
                    qty = targetContracts;
                    longDir = isLongDirection;
                    pendingTrade = false; // Consumimos la señal para evitar repetirla
                }
            }

            // Si el timer trajo una nueva orden válida, la ejecutamos en el gráfico
            if (execute)
            {
                // Opcional: Cerrar posición previa si deseas limpiar antes de la nueva orden
                if (Position.MarketPosition != MarketPosition.Flat)
                {
                    ExitLong();
                    ExitShort();
                }

                if (longDir)
                {
                    EnterLong(qty, "ApiLongEntry");
                    Print(string.Format(">>> Ejecutando compra LONG por {0} contratos.", qty));
                }
                else
                {
                    EnterShort(qty, "ApiShortEntry");
                    Print(string.Format(">>> Ejecutando venta SHORT por {0} contratos.", qty));
                }
            }
        }
    }
}