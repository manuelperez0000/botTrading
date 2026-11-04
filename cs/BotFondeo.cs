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
    public class FondeoBot2 : Strategy
    {
        // --- PARÁMETROS CONFIGURABLES ---
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Cantidad de Contratos", Order = 1, GroupName = "Parámetros de Operación", Description = "Número de contratos a enviar en cada orden")]
        public int CantidadContratos { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Ratio Riesgo/Beneficio Normal", Order = 2, GroupName = "Parámetros de Operación", Description = "Ratio de riesgo/beneficio normal")]
        public double RATIO_RB_NORMAL { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, double.MaxValue)]
        [Display(Name = "Stop Loss (Puntos)", Order = 3, GroupName = "Parámetros de Operación", Description = "Distancia del Stop Loss en puntos")]
        public double StopLossPuntos { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Máxima Pérdida ($)", Order = 4, GroupName = "Parámetros de Operación", Description = "Límite máximo de pérdida monetaria conjunta permitida")]
        public double MaxPerdidaDolares { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Máximo Grupos Diarios", Order = 5, GroupName = "Parámetros de Operación", Description = "Número máximo de grupos de operaciones permitidos al día")]
        public int MaxGruposDiarios { get; set; }


        // --- CONSTANTES Y VARIABLES DE CONTROL ---
        private const double RATIO_RB_CONTEXTO_CAMBIADO = 1.00;

        private int gruposHoy = 0;
        private DateTime fechaActual = DateTime.MinValue;

        // Indicadores Contexto 15M (BarsArray[1])
        private EMA ema50_15M;
        private EMA ema100_15M;
        private double ultimoMaximo15M = 0;
        private double ultimoMinimo15M = 0;
        private bool contextoAlcista15M = false;
        private bool contextoBajista15M = false;

        // Indicadores 1M (BarsArray[0])
        private EMA ema50_1M;
        private EMA ema100_1M;

        // Swings y Fibo 5M (BarsArray[2])
        private double swingOrigen5M = 0;
        private double swingExtremo5M = 0;
        private bool fiboFijado = false;
        private bool fiboEjecutado0758 = false;
        private bool fiboEjecutado0886 = false;
        private bool agregadoEjecutado = false;

        // Órdenes pendientes
        private Order ordenPending0758 = null;
        private Order ordenPending0886 = null;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"FondeoBot2 - Multitemporal optimizado (Contexto 15M, Fibo 5M, Ejecución 1M)";
                Name = "FondeoBot2";
                Calculate = Calculate.OnEachTick;
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

                CantidadContratos = 5;
                RATIO_RB_NORMAL = 1.4;
                StopLossPuntos = 2.0;
                MaxPerdidaDolares = 100.0;
                MaxGruposDiarios = 5; // Ampliado por defecto para mayor frecuencia diaria
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 15); // BarsArray[1] = Contexto 15M
                AddDataSeries(BarsPeriodType.Minute, 5);  // BarsArray[2] = Fibo / Swings 5M
            }
            else if (State == State.DataLoaded)
            {
                ema50_15M = EMA(BarsArray[1], 50);
                ema100_15M = EMA(BarsArray[1], 100);

                ema50_1M = EMA(BarsArray[0], 50);
                ema100_1M = EMA(BarsArray[0], 100);
            }
        }

        protected override void OnBarUpdate()
        {
            // Control estricto de PnL flotante en tiempo real (Tick a Tick)
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                double valorPunto = Instrument.MasterInstrument.PointValue;
                double pnlFlotante = 0;

                if (Position.MarketPosition == MarketPosition.Long)
                {
                    pnlFlotante = (Close[0] - Position.AveragePrice) * Position.Quantity * valorPunto;
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    pnlFlotante = (Position.AveragePrice - Close[0]) * Position.Quantity * valorPunto;
                }

                if (pnlFlotante <= -MaxPerdidaDolares)
                {
                    ExitLong();
                    ExitShort();
                    CancelPendingOrders();
                    return;
                }
            }

            // Procesar temporalidades secundarias cuando se actualizan
            if (BarsInProgress != 0)
            {
                if (CurrentBars[1] < 100 || CurrentBars[2] < 100)
                    return;

                if (BarsInProgress == 1)
                {
                    EvaluarContexto15M();
                }
                else if (BarsInProgress == 2)
                {
                    if (gruposHoy < MaxGruposDiarios)
                    {
                        ProcesarFibo5M();
                    }
                }
                return;
            }

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

            // --- EVALUACION DE AGREGADOS EN 1 MINUTO ---
            if (fiboFijado && !agregadoEjecutado && Position.MarketPosition != MarketPosition.Flat)
            {
                ProcesarAgregado1M();
            }
        }

        private void EvaluarContexto15M()
        {
            double precioCierre15M = Closes[1][0];
            double maximoActual15M = Highs[1][0];
            double minimoActual15M = Lows[1][0];

            if (ultimoMaximo15M == 0) ultimoMaximo15M = maximoActual15M;
            if (ultimoMinimo15M == 0) ultimoMinimo15M = minimoActual15M;

            bool anteriorAlcista = contextoAlcista15M;
            bool anteriorBajista = contextoBajista15M;

            if (maximoActual15M > ultimoMaximo15M && precioCierre15M > ema50_15M[0] && precioCierre15M > ema100_15M[0])
            {
                contextoAlcista15M = true;
                contextoBajista15M = false;

                if (!anteriorAlcista)
                {
                    AjustarObjetivosRatioReducido();
                    CancelPendingOrders();
                    ResetFiboState();
                }
            }
            else if (minimoActual15M < ultimoMinimo15M && precioCierre15M < ema50_15M[0] && precioCierre15M < ema100_15M[0])
            {
                contextoBajista15M = true;
                contextoAlcista15M = false;

                if (!anteriorBajista)
                {
                    AjustarObjetivosRatioReducido();
                    CancelPendingOrders();
                    ResetFiboState();
                }
            }

            ultimoMaximo15M = maximoActual15M;
            ultimoMinimo15M = minimoActual15M;
        }

        private void AjustarObjetivosRatioReducido()
        {
            if (Position.MarketPosition == MarketPosition.Long)
            {
                double precioEntrada = Position.AveragePrice;
                double stopPrice = swingOrigen5M - StopLossPuntos;
                double riesgoPuntos = precioEntrada - stopPrice;
                double nuevoTarget = precioEntrada + (riesgoPuntos * RATIO_RB_CONTEXTO_CAMBIADO);

                SetProfitTarget(CalculationMode.Price, nuevoTarget);
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                double precioEntrada = Position.AveragePrice;
                double stopPrice = swingOrigen5M + StopLossPuntos;
                double riesgoPuntos = stopPrice - precioEntrada;
                double nuevoTarget = precioEntrada - (riesgoPuntos * RATIO_RB_CONTEXTO_CAMBIADO);

                SetProfitTarget(CalculationMode.Price, nuevoTarget);
            }
        }

        private void ProcesarFibo5M()
        {
            bool esVelaRoja5M = Closes[2][0] < Opens[2][0];
            bool esVelaVerde5M = Closes[2][0] > Opens[2][0];

            if (contextoAlcista15M)
            {
                if (esVelaRoja5M && !fiboFijado)
                {
                    int lookback = 10;
                    swingOrigen5M = MIN(Lows[2], lookback)[1];
                    swingExtremo5M = Highs[2][1];
                    fiboFijado = true;

                    ColocarOrdenesLimite(true);
                }
                else if (!fiboFijado && Highs[2][0] > swingExtremo5M)
                {
                    swingExtremo5M = Highs[2][0];
                }

                if (fiboFijado && Lows[2][0] < swingOrigen5M)
                {
                    CancelPendingOrders();
                    ResetFiboState();
                }
            }
            else if (contextoBajista15M)
            {
                if (esVelaVerde5M && !fiboFijado)
                {
                    int lookback = 10;
                    swingOrigen5M = MAX(Highs[2], lookback)[1];
                    swingExtremo5M = Lows[2][1];
                    fiboFijado = true;

                    ColocarOrdenesLimite(false);
                }
                else if (!fiboFijado && Lows[2][0] < swingExtremo5M)
                {
                    swingExtremo5M = Lows[2][0];
                }

                if (fiboFijado && Highs[2][0] > swingExtremo5M)
                {
                    CancelPendingOrders();
                    ResetFiboState();
                }
            }
        }

        private void ColocarOrdenesLimite(bool esCompra)
        {
            double rango = Math.Abs(swingExtremo5M - swingOrigen5M);
            if (rango <= 0) return;

            double valorPunto = Instrument.MasterInstrument.PointValue;

            // Dividimos el riesgo máximo permitido entre 2 (50$ para cada nivel: 0.758 y 0.886)
            double maxRiesgoPorOrden = MaxPerdidaDolares / 2.0;
            double maxPuntosPermitidos = maxRiesgoPorOrden / (CantidadContratos * valorPunto);
            double stopEfectivo = Math.Min(StopLossPuntos, maxPuntosPermitidos);

            if (esCompra)
            {
                double nivel0758 = swingExtremo5M - (rango * 0.758);
                double nivel0886 = swingExtremo5M - (rango * 0.886);

                double stop0758 = nivel0758 - stopEfectivo;
                double stop0886 = nivel0886 - stopEfectivo;

                double riesgopuntos0758 = nivel0758 - stop0758;
                double riesgopuntos0886 = nivel0886 - stop0886;

                double target0758 = nivel0758 + (riesgopuntos0758 * RATIO_RB_NORMAL);
                double target0886 = nivel0886 + (riesgopuntos0886 * RATIO_RB_NORMAL);

                // Asignar Stop Loss y Profit Target nativos independientes para cada orden
                SetStopLoss("Fibo_0758_Long", CalculationMode.Price, stop0758, false);
                SetProfitTarget("Fibo_0758_Long", CalculationMode.Price, target0758);

                SetStopLoss("Fibo_0886_Long", CalculationMode.Price, stop0886, false);
                SetProfitTarget("Fibo_0886_Long", CalculationMode.Price, target0886);

                EnterLongLimit(0, true, CantidadContratos, nivel0758, "Fibo_0758_Long");
                EnterLongLimit(0, true, CantidadContratos, nivel0886, "Fibo_0886_Long");

                gruposHoy++;
            }
            else
            {
                double nivel0758 = swingExtremo5M + (rango * 0.758);
                double nivel0886 = swingExtremo5M + (rango * 0.886);

                double stop0758 = nivel0758 + stopEfectivo;
                double stop0886 = nivel0886 + stopEfectivo;

                double riesgopuntos0758 = stop0758 - nivel0758;
                double riesgopuntos0886 = stop0886 - nivel0886;

                double target0758 = nivel0758 - (riesgopuntos0758 * RATIO_RB_NORMAL);
                double target0886 = nivel0886 - (riesgopuntos0886 * RATIO_RB_NORMAL);

                SetStopLoss("Fibo_0758_Short", CalculationMode.Price, stop0758, false);
                SetProfitTarget("Fibo_0758_Short", CalculationMode.Price, target0758);

                SetStopLoss("Fibo_0886_Short", CalculationMode.Price, stop0886, false);
                SetProfitTarget("Fibo_0886_Short", CalculationMode.Price, target0886);

                EnterShortLimit(0, true, CantidadContratos, nivel0758, "Fibo_0758_Short");
                EnterShortLimit(0, true, CantidadContratos, nivel0886, "Fibo_0886_Short");

                gruposHoy++;
            }
        }
        private void ProcesarAgregado1M()
        {
            double precio1M = Closes[0][0];

            if (contextoAlcista15M && precio1M > ema50_1M[0] && precio1M > ema100_1M[0] && Highs[0][0] > Highs[0][1])
            {
                double stopHeredado = swingOrigen5M - StopLossPuntos;
                double riesgoPuntos = precio1M - stopHeredado;
                double targetAgregado = precio1M + (riesgoPuntos * RATIO_RB_NORMAL);

                SetStopLoss("Agregado_1M_Long", CalculationMode.Price, stopHeredado, false);
                SetProfitTarget("Agregado_1M_Long", CalculationMode.Price, targetAgregado);

                EnterLong(CantidadContratos, "Agregado_1M_Long");
                agregadoEjecutado = true;
            }
            else if (contextoBajista15M && precio1M < ema50_1M[0] && precio1M < ema100_1M[0] && Lows[0][0] < Lows[0][1])
            {
                double stopHeredado = swingOrigen5M + StopLossPuntos;
                double riesgoPuntos = stopHeredado - precio1M;
                double targetAgregado = precio1M - (riesgoPuntos * RATIO_RB_NORMAL);

                SetStopLoss("Agregado_1M_Short", CalculationMode.Price, stopHeredado, false);
                SetProfitTarget("Agregado_1M_Short", CalculationMode.Price, targetAgregado);

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
            swingOrigen5M = 0;
            swingExtremo5M = 0;
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