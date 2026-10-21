#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class TopstepBot3 : Strategy
    {
        // --- PARAMETRO PRINCIPAL DE CONTRATOS POR ORDEN ---
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Cantidad de Contratos", Order = 1, GroupName = "Parámetros de Operación", Description = "Número de contratos a enviar en cada orden (ej. 1, 2, 10)")]
        public int CantidadContratos { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Ratio Riesgo/Beneficio Normal", Order = 2, GroupName = "Parámetros de Operación", Description = "Ratio de riesgo/beneficio normal para calcular objetivos de ganancia")]
        public double RATIO_RB_NORMAL { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, double.MaxValue)]
        [Display(Name = "Stop Loss (Puntos)", Order = 3, GroupName = "Parámetros de Operación", Description = "Distancia del Stop Loss en puntos (ej. 2.0 para 8 ticks en MES)")]
        public double StopLossPuntos { get; set; }


        // --- PARAMETROS DE ENTRADA Y RIESGO ---
        private const double RATIO_RB_CONTEXTO_CAMBIADO = 1.00;
        private const int MAX_GRUPOS_DIARIOS = 2;

        // --- CONTROL DE ESTADO Y GRUPOS ---
        private int gruposHoy = 0;
        private DateTime fechaActual = DateTime.MinValue;

        // --- INDICADORES Y CONTEXTO 1H (BarsArray[1]) ---
        private EMA ema50_1H;
        private EMA ema100_1H;
        private double ultimoMaximo1H = 0;
        private double ultimoMinimo1H = 0;
        private bool contextoAlcista1H = false;
        private bool contextoBajista1H = false;

        // --- INDICADORES 1M (BarsArray[0]) PARA AGREGADOS ---
        private EMA ema50_1M;
        private EMA ema100_1M;

        // --- SWINGS Y FIBONACCI 15M (BarsArray[2]) ---
        private double swingOrigen15M = 0;
        private double swingExtremo15M = 0;
        private bool fiboFijado = false;
        private bool fiboEjecutado0758 = false;
        private bool fiboEjecutado0886 = false;
        private bool agregadoEjecutado = false;

        // --- ORDENES PENDIENTES ---
        private Order ordenPending0758 = null;
        private Order ordenPending0886 = null;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"TopstepBot3 - Estrategia de retrocesos Fibonacci multitemporal con contratos configurables";
                Name = "TopstepBot3";
                Calculate = Calculate.OnBarClose;

                // Permitir un límite alto de posiciones acumuladas por si colocas 10 o más contratos
                EntriesPerDirection = 100;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = true;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = true;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 100;

                // VALOR POR DEFECTO: 1 Contrato (Puedes cambiarlo en la interfaz a 2, 10, etc.)
                CantidadContratos = 5;
                RATIO_RB_NORMAL = 1.4;
                StopLossPuntos = 2.0;
            }
            else if (State == State.Configure)
            {
                // BarsArray[0] = 1M
                // BarsArray[1] = 1H
                AddDataSeries(BarsPeriodType.Minute, 60);

                // BarsArray[2] = 15M
                AddDataSeries(BarsPeriodType.Minute, 15);
            }
            else if (State == State.DataLoaded)
            {
                // EMAs 1H
                ema50_1H = EMA(BarsArray[1], 50);
                ema100_1H = EMA(BarsArray[1], 100);

                // EMAs 1M
                ema50_1M = EMA(BarsArray[0], 50);
                ema100_1M = EMA(BarsArray[0], 100);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < 100 || CurrentBars[1] < 100 || CurrentBars[2] < 100)
                return;

            // 1. REINICIO DIARIO
            if (Time[0].Date != fechaActual)
            {
                fechaActual = Time[0].Date;
                gruposHoy = 0;
                ResetFiboState();
            }

            // 2. HORARIOS Y CORTES DE SEGURIDAD TOPSTEP (Hora CT)
            int horaActual = ToTime(Time[0]);

            if (horaActual >= 150800 && horaActual < 151000)
            {
                CancelPendingOrders();
                return;
            }

            if (horaActual >= 151000)
            {
                if (Position.MarketPosition != MarketPosition.Flat)
                {
                    ExitLong();
                    ExitShort();
                }
                CancelPendingOrders();
                return;
            }

            if (horaActual >= 140800)
                return;

            // --- EVALUACION DE CONTEXTO EN 1 HORA (BarsInProgress == 1) ---
            if (BarsInProgress == 1)
            {
                EvaluarContexto1H();
                return;
            }

            // --- EVALUACION DE SWINGS Y FIBONACCI EN 15 MINUTOS (BarsInProgress == 2) ---
            if (BarsInProgress == 2)
            {
                if (gruposHoy < MAX_GRUPOS_DIARIOS)
                {
                    ProcesarFibo15M();
                }
                return;
            }

            // --- EVALUACION DE AGREGADOS EN 1 MINUTO (BarsInProgress == 0) ---
            if (BarsInProgress == 0 && fiboFijado && !agregadoEjecutado && Position.MarketPosition != MarketPosition.Flat)
            {
                ProcesarAgregado1M();
            }
        }

        private void EvaluarContexto1H()
        {
            double precioCierre1H = Closes[1][0];
            double maximoActual1H = Highs[1][0];
            double minimoActual1H = Lows[1][0];

            if (ultimoMaximo1H == 0) ultimoMaximo1H = maximoActual1H;
            if (ultimoMinimo1H == 0) ultimoMinimo1H = minimoActual1H;

            bool anteriorAlcista = contextoAlcista1H;
            bool anteriorBajista = contextoBajista1H;

            if (maximoActual1H > ultimoMaximo1H && precioCierre1H > ema50_1H[0] && precioCierre1H > ema100_1H[0])
            {
                contextoAlcista1H = true;
                contextoBajista1H = false;

                if (!anteriorAlcista)
                {
                    AjustarObjetivosRatioReducido();
                    CancelPendingOrders();
                    ResetFiboState();
                }
            }
            else if (minimoActual1H < ultimoMinimo1H && precioCierre1H < ema50_1H[0] && precioCierre1H < ema100_1H[0])
            {
                contextoBajista1H = true;
                contextoAlcista1H = false;

                if (!anteriorBajista)
                {
                    AjustarObjetivosRatioReducido();
                    CancelPendingOrders();
                    ResetFiboState();
                }
            }

            ultimoMaximo1H = maximoActual1H;
            ultimoMinimo1H = minimoActual1H;
        }

        private void AjustarObjetivosRatioReducido()
        {
            if (Position.MarketPosition == MarketPosition.Long)
            {
                double precioEntrada = Position.AveragePrice;
                double stopPrice = swingOrigen15M - StopLossPuntos;
                double riesgoPuntos = precioEntrada - stopPrice;
                double nuevoTarget = precioEntrada + (riesgoPuntos * RATIO_RB_CONTEXTO_CAMBIADO);

                SetProfitTarget(CalculationMode.Price, nuevoTarget);
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                double precioEntrada = Position.AveragePrice;
                double stopPrice = swingOrigen15M + StopLossPuntos;
                double riesgoPuntos = stopPrice - precioEntrada;
                double nuevoTarget = precioEntrada - (riesgoPuntos * RATIO_RB_CONTEXTO_CAMBIADO);

                SetProfitTarget(CalculationMode.Price, nuevoTarget);
            }
        }

        private void ProcesarFibo15M()
        {
            bool esVelaRoja15M = Closes[2][0] < Opens[2][0];
            bool esVelaVerde15M = Closes[2][0] > Opens[2][0];

            if (contextoAlcista1H)
            {
                if (esVelaRoja15M && !fiboFijado)
                {
                    int lookback = 10;
                    swingOrigen15M = MIN(Lows[2], lookback)[1];
                    swingExtremo15M = Highs[2][1];
                    fiboFijado = true;

                    ColocarOrdenesLimite(true);
                }
                else if (!fiboFijado && Highs[2][0] > swingExtremo15M)
                {
                    swingExtremo15M = Highs[2][0];
                }

                if (fiboFijado && Lows[2][0] < swingOrigen15M)
                {
                    CancelPendingOrders();
                    ResetFiboState();
                }
            }
            else if (contextoBajista1H)
            {
                if (esVelaVerde15M && !fiboFijado)
                {
                    int lookback = 10;
                    swingOrigen15M = MAX(Highs[2], lookback)[1];
                    swingExtremo15M = Lows[2][1];
                    fiboFijado = true;

                    ColocarOrdenesLimite(false);
                }
                else if (!fiboFijado && Lows[2][0] < swingExtremo15M)
                {
                    swingExtremo15M = Lows[2][0];
                }

                if (fiboFijado && Highs[2][0] > swingExtremo15M)
                {
                    CancelPendingOrders();
                    ResetFiboState();
                }
            }
        }

        private void ColocarOrdenesLimite(bool esCompra)
        {
            double rango = Math.Abs(swingExtremo15M - swingOrigen15M);
            if (rango <= 0) return;

            if (esCompra)
            {
                double nivel0758 = swingExtremo15M - (rango * 0.758);
                double nivel0886 = swingExtremo15M - (rango * 0.886);

                double stop0758 = nivel0886 - StopLossPuntos;
                double stop0886 = swingOrigen15M - StopLossPuntos;

                double riesgopuntos0758 = nivel0758 - stop0758;
                double riesgopuntos0886 = nivel0886 - stop0886;

                double target0758 = nivel0758 + (riesgopuntos0758 * RATIO_RB_NORMAL);
                double target0886 = nivel0886 + (riesgopuntos0886 * RATIO_RB_NORMAL);

                SetStopLoss("Fibo_0758_Long", CalculationMode.Price, stop0758, false);
                SetProfitTarget("Fibo_0758_Long", CalculationMode.Price, target0758);

                SetStopLoss("Fibo_0886_Long", CalculationMode.Price, stop0886, false);
                SetProfitTarget("Fibo_0886_Long", CalculationMode.Price, target0886);

                // SE APLICA LA VARIABLE CantidadContratos
                EnterLongLimit(0, true, CantidadContratos, nivel0758, "Fibo_0758_Long");
                EnterLongLimit(0, true, CantidadContratos, nivel0886, "Fibo_0886_Long");

                gruposHoy++;
            }
            else
            {
                double nivel0758 = swingExtremo15M + (rango * 0.758);
                double nivel0886 = swingExtremo15M + (rango * 0.886);

                double stop0758 = nivel0886 + StopLossPuntos;
                double stop0886 = swingOrigen15M + StopLossPuntos;

                double riesgopuntos0758 = stop0758 - nivel0758;
                double riesgopuntos0886 = stop0886 - nivel0886;

                double target0758 = nivel0758 - (riesgopuntos0758 * RATIO_RB_NORMAL);
                double target0886 = nivel0886 - (riesgopuntos0886 * RATIO_RB_NORMAL);

                SetStopLoss("Fibo_0758_Short", CalculationMode.Price, stop0758, false);
                SetProfitTarget("Fibo_0758_Short", CalculationMode.Price, target0758);

                SetStopLoss("Fibo_0886_Short", CalculationMode.Price, stop0886, false);
                SetProfitTarget("Fibo_0886_Short", CalculationMode.Price, target0886);

                // SE APLICA LA VARIABLE CantidadContratos
                EnterShortLimit(0, true, CantidadContratos, nivel0758, "Fibo_0758_Short");
                EnterShortLimit(0, true, CantidadContratos, nivel0886, "Fibo_0886_Short");

                gruposHoy++;
            }
        }

        private void ProcesarAgregado1M()
        {
            double precio1M = Closes[0][0];

            if (contextoAlcista1H && precio1M > ema50_1M[0] && precio1M > ema100_1M[0] && Highs[0][0] > Highs[0][1])
            {
                double stopHeredado = swingOrigen15M - StopLossPuntos;
                double riesgoPuntos = precio1M - stopHeredado;
                double targetAgregado = precio1M + (riesgoPuntos * RATIO_RB_NORMAL);

                SetStopLoss("Agregado_1M_Long", CalculationMode.Price, stopHeredado, false);
                SetProfitTarget("Agregado_1M_Long", CalculationMode.Price, targetAgregado);

                // SE APLICA LA VARIABLE CantidadContratos
                EnterLong(CantidadContratos, "Agregado_1M_Long");
                agregadoEjecutado = true;
            }
            else if (contextoBajista1H && precio1M < ema50_1M[0] && precio1M < ema100_1M[0] && Lows[0][0] < Lows[0][1])
            {
                double stopHeredado = swingOrigen15M + StopLossPuntos;
                double riesgoPuntos = stopHeredado - precio1M;
                double targetAgregado = precio1M - (riesgoPuntos * RATIO_RB_NORMAL);

                SetStopLoss("Agregado_1M_Short", CalculationMode.Price, stopHeredado, false);
                SetProfitTarget("Agregado_1M_Short", CalculationMode.Price, targetAgregado);

                // SE APLICA LA VARIABLE CantidadContratos
                EnterShort(CantidadContratos, "Agregado_1M_Short");
                agregadoEjecutado = true;
            }
        }

        private void CancelPendingOrders()
        {
            if (ordenPending0758 != null && (ordenPending0758.OrderState == OrderState.Working || ordenPending0758.OrderState == OrderState.Accepted))
                CancelOrder(ordenPending0758);

            if (ordenPending0886 != null && (ordenPending0886.OrderState == OrderState.Working || ordenPending0886.OrderState == OrderState.Accepted))
                CancelOrder(ordenPending0886);
        }

        private void ResetFiboState()
        {
            fiboFijado = false;
            fiboEjecutado0758 = false;
            fiboEjecutado0886 = false;
            agregadoEjecutado = false;
            swingOrigen15M = 0;
            swingExtremo15M = 0;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order != null && execution.Order.Name.Contains("0758") && execution.Order.OrderState == OrderState.Filled)
            {
                fiboEjecutado0758 = true;
            }

            if (fiboEjecutado0758 && !fiboEjecutado0886 && SystemPerformance.AllTrades.Count > 0)
            {
                Trade ultimoTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                if (ultimoTrade.ProfitCurrency > 0)
                {
                    CancelPendingOrders();
                }
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order.Name.Contains("0758")) ordenPending0758 = order;
            if (order.Name.Contains("0886")) ordenPending0886 = order;
        }
    }
}