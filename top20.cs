#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
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

// fondeoTopStep — basado en TopstepBot3.
// Cambios:
//  1. Los contratos se calculan para ganar ProfitObjetivoUSD ($200) en cada operación.
//  2. El stop se calcula para perder como máximo RiesgoMaximoUSD ($100) repartido entre
//     las operaciones activas: 1 op = $100, 2 ops = $50 cada una, 3 ops = $33.33 cada una.
//     Al entrar una operación nueva, los stops activos se acercan (nunca se alejan).
//  3. Horario de operación en hora de Nueva York (ET), configurable en el panel:
//     ventana 1 = 6:00–9:30, ventana 2 = 9:30–16:00, horario a evitar = 16:00–18:00.
//     Fuera de las ventanas no se abren operaciones nuevas; en el horario a evitar se cierra todo.

// Modos de cálculo de contratos y stop
public enum ModoTamanoFondeo
{
    ProfitConStopEstructural,   // contratos para ganar el profit objetivo; stop estructural del bot original
    PorRiesgo,                  // stop estructural; contratos para no pasar del riesgo por operación
    ProfitConStopAjustado       // contratos para ganar el profit objetivo; stop acercado al riesgo en $
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public class fondeoTopStep2 : Strategy
    {
        // --- PARAMETROS DE TAMAÑO Y RIESGO EN DOLARES ---
        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Profit objetivo por operación ($)", Order = 1, GroupName = "Parámetros de Operación", Description = "Los contratos se calculan para ganar esta cantidad al llegar al objetivo")]
        public double ProfitObjetivoUSD { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Riesgo máximo total ($)", Order = 2, GroupName = "Parámetros de Operación", Description = "Se reparte entre las operaciones activas: 1 = 100%, 2 = 50% cada una, 3 = 33% cada una")]
        public double RiesgoMaximoUSD { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Máx. contratos por operación", Order = 3, GroupName = "Parámetros de Operación", Description = "Tope de seguridad de contratos por orden")]
        public int MaxContratos { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Modo de tamaño", Order = 6, GroupName = "Parámetros de Operación", Description = "ProfitConStopEstructural: contratos para ganar el profit objetivo con el stop del bot original (la pérdida por operación ≈ profit / ratio). PorRiesgo: contratos para no pasar del riesgo máximo. ProfitConStopAjustado: contratos para el profit objetivo y stop acercado al riesgo en $.")]
        public ModoTamanoFondeo ModoTamano { get; set; }

        [NinjaScriptProperty]
        [Range(0, 90)]
        [Display(Name = "Reducción del stop (%)", Order = 8, GroupName = "Parámetros de Operación", Description = "Acerca el stop estructural a la entrada este porcentaje. El objetivo no cambia. 30 = el stop queda al 70% de su distancia original.")]
        public double ReduccionStopPct { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Máx. operaciones por día", Order = 1, GroupName = "Límite Diario", Description = "Al activarse esta operación se cancelan las demás órdenes pendientes y no se abren agregados.")]
        public int MaxOperacionesDiarias { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Ganancia máxima diaria ($)", Order = 2, GroupName = "Límite Diario", Description = "Al llegar a esta ganancia (cerrada + abierta) se cierra todo y no se opera más ese día.")]
        public double GananciaMaxDiariaUSD { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Pérdida máxima diaria ($)", Order = 3, GroupName = "Límite Diario", Description = "Al llegar a esta pérdida (cerrada + abierta) se cierra todo y no se opera más ese día.")]
        public double PerdidaMaxDiariaUSD { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Comisión por contrato ida y vuelta ($)", Order = 9, GroupName = "Parámetros de Operación", Description = "Se descuenta del riesgo para que la pérdida total no pase del límite. El deslizamiento se toma del ajuste Slippage de la estrategia.")]
        public double ComisionPorContratoUSD { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Prioridad profit", Order = 10, GroupName = "Parámetros de Operación", Description = "Si la pérdida pasa del límite: activado = se acerca el stop y se mantiene el profit objetivo; desactivado = se mantiene el stop y se reducen contratos (menos profit).")]
        public bool PrioridadProfit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Permitir ventas", Order = 7, GroupName = "Parámetros de Operación", Description = "El bot original casi no vendía porque cancelaba los Fibonacci bajistas al instante.")]
        public bool PermitirVentas { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, double.MaxValue)]
        [Display(Name = "Ratio Riesgo/Beneficio Normal", Order = 4, GroupName = "Parámetros de Operación", Description = "Ratio de riesgo/beneficio normal para calcular objetivos de ganancia")]
        public double RATIO_RB_NORMAL { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, double.MaxValue)]
        [Display(Name = "Stop Loss (Puntos)", Order = 5, GroupName = "Parámetros de Operación", Description = "Distancia estructural en puntos, usada para medir el objetivo (ej. 2.0 para 8 ticks en MES)")]
        public double StopLossPuntos { get; set; }

        // --- FIBONACCI Y SWINGS (15M) ---
        [NinjaScriptProperty]
        [Range(0.01, 1.0)]
        [Display(Name = "Nivel Fibonacci 1", Order = 1, GroupName = "Fibonacci y Swings", Description = "Primer nivel de entrada (el menos profundo). Por defecto 0.758. Su stop estructural va por debajo del nivel 2.")]
        public double NivelFibo1 { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 1.0)]
        [Display(Name = "Nivel Fibonacci 2", Order = 2, GroupName = "Fibonacci y Swings", Description = "Segundo nivel de entrada (el más profundo). Por defecto 0.886. Su stop estructural va por debajo del origen del swing. Si es menor que el nivel 1, se intercambian.")]
        public double NivelFibo2 { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name = "Velas para buscar el swing", Order = 3, GroupName = "Fibonacci y Swings", Description = "Velas de 15 min hacia atrás para buscar el origen del swing (mínimo en compras, máximo en ventas). Por defecto 10.")]
        public int VelasSwing { get; set; }

        // --- HORARIO DE OPERACION (HORA DE NUEVA YORK - ET) ---
        [NinjaScriptProperty]
        [Display(Name = "Usar horario de operación", Order = 1, GroupName = "Horario de Operación (ET)", Description = "Activado: solo se abren operaciones dentro de las ventanas. Las horas son de Nueva York (ET), sin importar la zona horaria del PC.")]
        public bool UsarHorario { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Ventana 1 - inicio", Order = 2, GroupName = "Horario de Operación (ET)", Description = "Inicio de la primera ventana de operación (ET). Por defecto 6:00 a.m.")]
        public DateTime Ventana1Inicio { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Ventana 1 - fin", Order = 3, GroupName = "Horario de Operación (ET)", Description = "Fin de la primera ventana de operación (ET). Por defecto 9:30 a.m. Inicio = fin desactiva la ventana.")]
        public DateTime Ventana1Fin { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Ventana 2 - inicio", Order = 4, GroupName = "Horario de Operación (ET)", Description = "Inicio de la segunda ventana de operación (ET). Por defecto 9:30 a.m.")]
        public DateTime Ventana2Inicio { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Ventana 2 - fin", Order = 5, GroupName = "Horario de Operación (ET)", Description = "Fin de la segunda ventana de operación (ET). Por defecto 4:00 p.m. Inicio = fin desactiva la ventana.")]
        public DateTime Ventana2Fin { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Evitar - inicio", Order = 6, GroupName = "Horario de Operación (ET)", Description = "Inicio del horario a evitar: cierre y pausa del mercado (ET). Por defecto 4:00 p.m.")]
        public DateTime EvitarInicio { get; set; }

        [NinjaScriptProperty]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Evitar - fin", Order = 7, GroupName = "Horario de Operación (ET)", Description = "Fin del horario a evitar (ET). Por defecto 6:00 p.m.")]
        public DateTime EvitarFin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cerrar posiciones en horario a evitar", Order = 8, GroupName = "Horario de Operación (ET)", Description = "Activado: al entrar en el horario a evitar se cierran las posiciones abiertas. Las órdenes pendientes se cancelan siempre.")]
        public bool CerrarEnHorarioEvitar { get; set; }


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
        private bool fiboEjecutadoN1 = false;
        private bool fiboEjecutadoN2 = false;
        private bool agregadoEjecutado = false;
        private bool grupoConsumido = false;   // se llenó la primera orden del Fibonacci actual

        // --- LIMITE DIARIO ---
        private int operacionesHoy = 0;
        private bool diaCerrado = false;       // se alcanzó +ganancia o -pérdida máxima del día
        private double pnlInicioDia = 0;
        private TimeZoneInfo zonaCT;
        private TimeZoneInfo zonaET;

        // --- ORDENES PENDIENTES ---
        private Order ordenPendingN1 = null;
        private Order ordenPendingN2 = null;

        // --- SEGUIMIENTO POR OPERACION (para tamaño y stops en $) ---
        private int dirGrupo = 0;   // 1 = compra, -1 = venta
        private readonly Dictionary<string, double> entradaPrevista = new Dictionary<string, double>();
        private readonly Dictionary<string, double> entradaReal     = new Dictionary<string, double>();
        private readonly Dictionary<string, double> stopPorSenal    = new Dictionary<string, double>();
        private readonly Dictionary<string, double> targetPorSenal  = new Dictionary<string, double>();
        private readonly Dictionary<string, int>    qtyPorSenal     = new Dictionary<string, int>();
        private readonly HashSet<string> senalesAbiertas = new HashSet<string>();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"fondeoTopStep - Fibonacci multitemporal con contratos calculados por $ de profit y riesgo repartido";
                Name = "fondeoTopStep2";
                Calculate = Calculate.OnBarClose;

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

                ProfitObjetivoUSD = 200;
                RiesgoMaximoUSD = 100;
                MaxContratos = 50;
                ModoTamano = ModoTamanoFondeo.ProfitConStopEstructural;
                PermitirVentas = false;
                ReduccionStopPct = 30;
                MaxOperacionesDiarias = 1;
                GananciaMaxDiariaUSD = 200;
                PerdidaMaxDiariaUSD = 100;
                ComisionPorContratoUSD = 1.30;
                PrioridadProfit = true;
                RATIO_RB_NORMAL = 1.4;
                StopLossPuntos = 2.0;

                // Fibonacci y swings
                NivelFibo1 = 0.758;
                NivelFibo2 = 0.886;
                VelasSwing = 10;

                // Horario (hora de Nueva York - ET)
                UsarHorario = true;
                Ventana1Inicio = DateTime.Parse("06:00", CultureInfo.InvariantCulture);
                Ventana1Fin    = DateTime.Parse("09:30", CultureInfo.InvariantCulture);
                Ventana2Inicio = DateTime.Parse("09:30", CultureInfo.InvariantCulture);
                Ventana2Fin    = DateTime.Parse("16:00", CultureInfo.InvariantCulture);
                EvitarInicio   = DateTime.Parse("16:00", CultureInfo.InvariantCulture);
                EvitarFin      = DateTime.Parse("18:00", CultureInfo.InvariantCulture);
                CerrarEnHorarioEvitar = true;
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

                zonaCT = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
                zonaET = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < 100 || CurrentBars[1] < 100 || CurrentBars[2] < 100)
                return;

            if (Position.MarketPosition == MarketPosition.Flat)
                senalesAbiertas.Clear();

            // Hora CT real, sin importar la zona horaria del PC
            DateTime horaCT = ToCT(Time[0]);
            int horaActual = ToTime(horaCT);

            // Hora de Nueva York (ET) para las ventanas de operación
            int horaET = ToTime(ToET(Time[0]));

            // 1. REINICIO DIARIO (fecha CT). Solo se borra el Fibonacci si no hay nada abierto.
            if (horaCT.Date != fechaActual)
            {
                fechaActual = horaCT.Date;
                gruposHoy = 0;
                operacionesHoy = 0;
                diaCerrado = false;
                pnlInicioDia = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                if (Position.MarketPosition == MarketPosition.Flat && !EstaViva(ordenPendingN1) && !EstaViva(ordenPendingN2))
                    ResetFiboState();
            }

            // 2. HORARIOS Y CORTES DE SEGURIDAD TOPSTEP (Hora CT)
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

            if (horaActual >= 150800)
            {
                CancelPendingOrders();
                return;
            }

            // 2b. HORARIO A EVITAR (ET): cierre y pausa del mercado. Se cancela todo y no se opera.
            if (UsarHorario && EnRangoHorario(horaET, EvitarInicio, EvitarFin))
            {
                CancelPendingOrders();
                if (CerrarEnHorarioEvitar)
                {
                    if (Position.MarketPosition == MarketPosition.Long)  ExitLong();
                    if (Position.MarketPosition == MarketPosition.Short) ExitShort();
                }
                if (fiboFijado && !grupoConsumido)
                    ResetFiboState();
                return;
            }

            // 14:08: no se abren grupos nuevos. Un Fibonacci sin ninguna orden llena abriría
            // un grupo nuevo, así que se cancela. Lo ya abierto se sigue gestionando.
            bool permiteGruposNuevos = horaActual < 140800;
            if (!permiteGruposNuevos && fiboFijado && !grupoConsumido)
            {
                CancelPendingOrders();
                ResetFiboState();
            }

            // 2c. VENTANAS DE OPERACION (ET): fuera de ellas no se abren operaciones nuevas.
            // Se cancelan las órdenes pendientes; lo ya abierto sigue con su stop y objetivo.
            bool dentroHorario = !UsarHorario
                              || EnRangoHorario(horaET, Ventana1Inicio, Ventana1Fin)
                              || EnRangoHorario(horaET, Ventana2Inicio, Ventana2Fin);
            if (!dentroHorario)
            {
                CancelPendingOrders();
                if (fiboFijado && !grupoConsumido)
                    ResetFiboState();
                permiteGruposNuevos = false;
            }

            // 3. LIMITE DIARIO DE RESULTADO: +$200 o -$100 (cerrado + abierto) -> se cierra todo por hoy
            if (!diaCerrado)
            {
                double pnlDia = PnlDelDia();
                if (pnlDia >= GananciaMaxDiariaUSD || pnlDia <= -PerdidaMaxDiariaUSD)
                {
                    CancelPendingOrders();
                    if (Position.MarketPosition == MarketPosition.Long)  ExitLong();
                    if (Position.MarketPosition == MarketPosition.Short) ExitShort();
                    diaCerrado = true;
                    Print(string.Format("{0} Límite diario alcanzado (${1:0.00}). No se opera más hoy.", Time[0], pnlDia));
                }
            }

            // 4. LIMITE DE OPERACIONES DIARIAS: con el día cerrado o el cupo usado no se abre nada nuevo
            bool cupoAgotado = diaCerrado || operacionesHoy >= MaxOperacionesDiarias;
            if (cupoAgotado && fiboFijado && !grupoConsumido)
            {
                CancelPendingOrders();
                ResetFiboState();
            }
            if (cupoAgotado)
                permiteGruposNuevos = false;

            // --- EVALUACION DE CONTEXTO EN 1 HORA (BarsInProgress == 1) ---
            if (BarsInProgress == 1)
            {
                EvaluarContexto1H();
                return;
            }

            // --- EVALUACION DE SWINGS Y FIBONACCI EN 15 MINUTOS (BarsInProgress == 2) ---
            if (BarsInProgress == 2)
            {
                ProcesarFibo15M(permiteGruposNuevos && gruposHoy < MAX_GRUPOS_DIARIOS);
                return;
            }

            // --- EVALUACION DE AGREGADOS EN 1 MINUTO (BarsInProgress == 0) ---
            if (BarsInProgress == 0 && dentroHorario && !cupoAgotado && fiboFijado && !agregadoEjecutado && Position.MarketPosition != MarketPosition.Flat)
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

        // Cambio de contexto: cada operación abierta pasa a objetivo 1:1 respecto a su stop en $
        private void AjustarObjetivosRatioReducido()
        {
            if (Position.MarketPosition == MarketPosition.Flat) return;

            foreach (string senal in senalesAbiertas.ToList())
            {
                if (!stopPorSenal.ContainsKey(senal)) continue;
                double entrada = entradaReal.ContainsKey(senal) ? entradaReal[senal] : Position.AveragePrice;
                double stop    = stopPorSenal[senal];
                double nuevoTarget = Redondear(entrada + (entrada - stop) * RATIO_RB_CONTEXTO_CAMBIADO);

                SetProfitTarget(senal, CalculationMode.Price, nuevoTarget);
                targetPorSenal[senal] = nuevoTarget;
            }
        }

        private void ProcesarFibo15M(bool permiteNuevoFibo)
        {
            bool esVelaRoja15M = Closes[2][0] < Opens[2][0];
            bool esVelaVerde15M = Closes[2][0] > Opens[2][0];

            if (contextoAlcista1H)
            {
                if (esVelaRoja15M && !fiboFijado && permiteNuevoFibo)
                {
                    int lookback = VelasSwing;
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
                if (esVelaVerde15M && !fiboFijado && permiteNuevoFibo && PermitirVentas)
                {
                    int lookback = VelasSwing;
                    swingOrigen15M = MAX(Highs[2], lookback)[1];
                    swingExtremo15M = Lows[2][1];
                    fiboFijado = true;

                    ColocarOrdenesLimite(false);
                }
                else if (!fiboFijado && Lows[2][0] < swingExtremo15M)
                {
                    swingExtremo15M = Lows[2][0];
                }

                // Invalidación: el precio rompe el máximo de origen del swing
                if (fiboFijado && Highs[2][0] > swingOrigen15M)
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

            dirGrupo = esCompra ? 1 : -1;
            LimpiarSeguimiento();

            // Con los 2 niveles colocados hay 2 operaciones ($50 cada una), salvo que el cupo
            // diario sea 1: entonces solo una puede activarse y le toca el riesgo completo.
            double riesgoPorOp = RiesgoPorOperacion(2);
            bool algunaOrden = false;

            // Nivel 1 = el menos profundo, nivel 2 = el más profundo (aunque se escriban al revés)
            double fibo1 = Math.Min(NivelFibo1, NivelFibo2);
            double fibo2 = Math.Max(NivelFibo1, NivelFibo2);

            if (esCompra)
            {
                double nivelN1 = Redondear(swingExtremo15M - (rango * fibo1));
                double nivelN2 = Redondear(swingExtremo15M - (rango * fibo2));

                // Stops estructurales: solo para medir el objetivo
                double stopEstrN1 = nivelN2 - StopLossPuntos;
                double stopEstrN2 = swingOrigen15M - StopLossPuntos;

                double targetN1 = Redondear(nivelN1 + ((nivelN1 - stopEstrN1) * RATIO_RB_NORMAL));
                double targetN2 = Redondear(nivelN2 + ((nivelN2 - stopEstrN2) * RATIO_RB_NORMAL));

                int qtyN1, qtyN2;
                double stopN1 = Dimensionar(nivelN1, stopEstrN1, targetN1, riesgoPorOp, out qtyN1);
                double stopN2 = Dimensionar(nivelN2, stopEstrN2, targetN2, riesgoPorOp, out qtyN2);

                // El grupo se cuenta al llenarse la primera orden (OnExecutionUpdate)
                if (qtyN1 > 0)
                {
                    RegistrarPlan("Fibo_N1_Long", nivelN1, stopN1, targetN1, qtyN1);
                    EnterLongLimit(0, true, qtyN1, nivelN1, "Fibo_N1_Long");
                    algunaOrden = true;
                }
                if (qtyN2 > 0)
                {
                    RegistrarPlan("Fibo_N2_Long", nivelN2, stopN2, targetN2, qtyN2);
                    EnterLongLimit(0, true, qtyN2, nivelN2, "Fibo_N2_Long");
                    algunaOrden = true;
                }
            }
            else
            {
                double nivelN1 = Redondear(swingExtremo15M + (rango * fibo1));
                double nivelN2 = Redondear(swingExtremo15M + (rango * fibo2));

                double stopEstrN1 = nivelN2 + StopLossPuntos;
                double stopEstrN2 = swingOrigen15M + StopLossPuntos;

                double targetN1 = Redondear(nivelN1 - ((stopEstrN1 - nivelN1) * RATIO_RB_NORMAL));
                double targetN2 = Redondear(nivelN2 - ((stopEstrN2 - nivelN2) * RATIO_RB_NORMAL));

                int qtyN1, qtyN2;
                double stopN1 = Dimensionar(nivelN1, stopEstrN1, targetN1, riesgoPorOp, out qtyN1);
                double stopN2 = Dimensionar(nivelN2, stopEstrN2, targetN2, riesgoPorOp, out qtyN2);

                if (qtyN1 > 0)
                {
                    RegistrarPlan("Fibo_N1_Short", nivelN1, stopN1, targetN1, qtyN1);
                    EnterShortLimit(0, true, qtyN1, nivelN1, "Fibo_N1_Short");
                    algunaOrden = true;
                }
                if (qtyN2 > 0)
                {
                    RegistrarPlan("Fibo_N2_Short", nivelN2, stopN2, targetN2, qtyN2);
                    EnterShortLimit(0, true, qtyN2, nivelN2, "Fibo_N2_Short");
                    algunaOrden = true;
                }
            }

            // Si ninguna orden cabe en el riesgo, se libera el Fibonacci para buscar otro
            if (!algunaOrden)
                ResetFiboState();
        }

        private void ProcesarAgregado1M()
        {
            double precio1M = Closes[0][0];

            bool senalLong  = contextoAlcista1H && precio1M > ema50_1M[0] && precio1M > ema100_1M[0] && Highs[0][0] > Highs[0][1];
            bool senalShort = contextoBajista1H && precio1M < ema50_1M[0] && precio1M < ema100_1M[0] && Lows[0][0] < Lows[0][1];
            if (!senalLong && !senalShort) return;

            dirGrupo = senalLong ? 1 : -1;
            string senal = senalLong ? "Agregado_1M_Long" : "Agregado_1M_Short";

            double stopHeredado   = senalLong ? swingOrigen15M - StopLossPuntos : swingOrigen15M + StopLossPuntos;
            double targetAgregado = Redondear(precio1M + (precio1M - stopHeredado) * RATIO_RB_NORMAL);

            // Con el agregado hay una operación activa más: el riesgo se reparte de nuevo
            int nOps = OperacionesActivas() + 1;
            double riesgoPorOp = RiesgoPorOperacion(nOps);

            int qty;
            double stop = Dimensionar(precio1M, stopHeredado, targetAgregado, riesgoPorOp, out qty);
            if (qty <= 0) { agregadoEjecutado = true; return; }

            // Nunca más lejos que el stop heredado
            stop = senalLong ? Math.Max(stop, Redondear(stopHeredado)) : Math.Min(stop, Redondear(stopHeredado));

            // Acercar los stops de las operaciones ya activas al nuevo riesgo por operación
            // (no aplica con stop estructural para $200: ahí los stops no se mueven)
            if (ModoTamano != ModoTamanoFondeo.ProfitConStopEstructural)
                ReajustarStops(riesgoPorOp);

            RegistrarPlan(senal, precio1M, stop, targetAgregado, qty);

            if (senalLong) EnterLong(qty, senal);
            else           EnterShort(qty, senal);

            agregadoEjecutado = true;
            Print(string.Format("{0} Agregado: {1} operaciones activas, riesgo ${2:0.00} cada una.", Time[0], nOps, riesgoPorOp));
        }

        private void CancelPendingOrders()
        {
            if (EstaViva(ordenPendingN1)) CancelOrder(ordenPendingN1);
            if (EstaViva(ordenPendingN2)) CancelOrder(ordenPendingN2);
        }

        private void ResetFiboState()
        {
            fiboFijado = false;
            fiboEjecutadoN1 = false;
            fiboEjecutadoN2 = false;
            agregadoEjecutado = false;
            grupoConsumido = false;
            swingOrigen15M = 0;
            swingExtremo15M = 0;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            Order o = execution.Order;

            // --- Seguimiento de operaciones abiertas (para el reparto de riesgo) ---
            if (o != null)
            {
                bool esEntrada = o.Name.StartsWith("Fibo_") || o.Name.StartsWith("Agregado_");
                if (esEntrada)
                {
                    entradaReal[o.Name] = o.AverageFillPrice;
                    bool operacionNueva = !senalesAbiertas.Contains(o.Name);
                    senalesAbiertas.Add(o.Name);

                    // Cupo diario: al activarse la operación del día se cancelan las demás órdenes pendientes
                    if (operacionNueva)
                    {
                        operacionesHoy++;
                        if (operacionesHoy >= MaxOperacionesDiarias)
                        {
                            CancelPendingOrders();
                            Print(string.Format("{0} Operación {1}/{2} del día: se cancelan las órdenes pendientes.", time, operacionesHoy, MaxOperacionesDiarias));
                        }
                    }

                    if (o.Name.Contains("_N1_")) fiboEjecutadoN1 = true;
                    if (o.Name.Contains("_N2_")) fiboEjecutadoN2 = true;

                    // El grupo se consume al activarse la primera orden del Fibonacci
                    if (o.Name.StartsWith("Fibo_") && fiboFijado && !grupoConsumido)
                    {
                        grupoConsumido = true;
                        gruposHoy++;
                        Print(string.Format("{0} Grupo consumido ({1}/{2}).", time, gruposHoy, MAX_GRUPOS_DIARIOS));
                    }

                    // Agregado a mercado: desplazar stop y objetivo según el precio real de llenado
                    if (o.Name.StartsWith("Agregado_") && o.OrderState == OrderState.Filled
                        && entradaPrevista.ContainsKey(o.Name) && stopPorSenal.ContainsKey(o.Name) && targetPorSenal.ContainsKey(o.Name))
                    {
                        double delta = o.AverageFillPrice - entradaPrevista[o.Name];
                        if (delta != 0)
                        {
                            stopPorSenal[o.Name]   = Redondear(stopPorSenal[o.Name] + delta);
                            targetPorSenal[o.Name] = Redondear(targetPorSenal[o.Name] + delta);
                            SetStopLoss(o.Name, CalculationMode.Price, stopPorSenal[o.Name], false);
                            SetProfitTarget(o.Name, CalculationMode.Price, targetPorSenal[o.Name]);
                        }
                    }
                }
                else if (o.OrderState == OrderState.Filled && !string.IsNullOrEmpty(o.FromEntrySignal))
                {
                    senalesAbiertas.Remove(o.FromEntrySignal);

                    // Regla cruzada: el nivel 1 llega a su objetivo antes de activarse el nivel 2 -> cancelar el nivel 2.
                    // Si el nivel 1 toca su stop, el nivel 2 sigue activo.
                    if (o.Name == "Profit target" && o.FromEntrySignal.Contains("_N1_")
                        && !fiboEjecutadoN2 && EstaViva(ordenPendingN2))
                    {
                        CancelOrder(ordenPendingN2);
                        Print(string.Format("{0} Nivel 1 en objetivo: se cancela el nivel 2 pendiente.", time));
                    }
                }
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order.Name.Contains("_N1_")) ordenPendingN1 = order;
            if (order.Name.Contains("_N2_")) ordenPendingN2 = order;
        }

        #region Tamaño de posición y riesgo en dólares

        // Calcula contratos y stop de una operación según el modo elegido.
        // En todos los modos la pérdida total (stop + comisión + deslizamiento) no pasa de riesgoUSD.
        // Devuelve qty = 0 si la operación no cabe en el riesgo (no se coloca).
        //  - ProfitConStopEstructural: contratos para ganar ProfitObjetivoUSD con el stop estructural
        //    reducido. Si esa pérdida pasa del riesgo: se acerca el stop (PrioridadProfit = true)
        //    o se reducen los contratos (PrioridadProfit = false).
        //  - PorRiesgo: stop estructural reducido y contratos para perder como máximo riesgoUSD.
        //  - ProfitConStopAjustado: contratos para ganar ProfitObjetivoUSD y stop ajustado al riesgo.
        private double Dimensionar(double entrada, double stopEstructural, double target, double riesgoUSD, out int qty)
        {
            double valorTick = Instrument.MasterInstrument.PointValue * TickSize;
            double costo     = CostoPorContrato();
            double stopRed   = StopReducido(entrada, stopEstructural);
            int    ticksRed  = TicksEntre(entrada, stopRed);
            double stop;

            if (ModoTamano == ModoTamanoFondeo.ProfitConStopEstructural)
            {
                qty = ContratosParaProfit(entrada, target);
                int ticksMax = TicksMaxParaRiesgo(qty, riesgoUSD);

                if (ticksMax >= ticksRed)
                    stop = stopRed;                                   // ya cabe en el riesgo
                else if (PrioridadProfit && ticksMax >= 1)
                    stop = StopATicks(entrada, ticksMax);             // mismo profit, stop más cerca
                else
                {
                    qty  = ContratosParaRiesgo(ticksRed, riesgoUSD);  // mismo stop, menos contratos
                    stop = stopRed;
                }
            }
            else if (ModoTamano == ModoTamanoFondeo.PorRiesgo)
            {
                qty  = ContratosParaRiesgo(ticksRed, riesgoUSD);
                stop = stopRed;
            }
            else
            {
                qty  = ContratosParaProfit(entrada, target);
                int ticksMax = TicksMaxParaRiesgo(qty, riesgoUSD);
                if (ticksMax < 1)
                {
                    qty      = ContratosParaRiesgo(1, riesgoUSD);
                    ticksMax = 1;
                }
                stop = StopATicks(entrada, ticksMax);
            }

            if (qty <= 0)
                Print(string.Format("{0} Operación omitida: ni 1 contrato cabe en el riesgo de ${1:0.00}.", Time[0], riesgoUSD));
            else
            {
                double perdida = qty * (TicksEntre(entrada, stop) * valorTick + costo);
                Print(string.Format("{0} Tamaño: {1} contratos, pérdida máxima ${2:0.00} (incluye comisión y deslizamiento), límite ${3:0.00}.",
                    Time[0], qty, perdida, riesgoUSD));
            }
            return stop;
        }

        // Comisión de ida y vuelta + deslizamiento de la salida por stop, por contrato
        private double CostoPorContrato()
        {
            double valorTick = Instrument.MasterInstrument.PointValue * TickSize;
            return ComisionPorContratoUSD + Slippage * valorTick;
        }

        // Ticks máximos de stop para que qty contratos no pierdan más de riesgoUSD con costos
        private int TicksMaxParaRiesgo(int qty, double riesgoUSD)
        {
            if (qty <= 0) return 0;
            double valorTick = Instrument.MasterInstrument.PointValue * TickSize;
            return (int)Math.Floor((riesgoUSD - qty * CostoPorContrato()) / (qty * valorTick) + 1e-9);
        }

        // Contratos que caben en riesgoUSD con un stop de 'ticks' ticks (con costos)
        private int ContratosParaRiesgo(int ticks, double riesgoUSD)
        {
            double valorTick = Instrument.MasterInstrument.PointValue * TickSize;
            double perdidaPorContrato = ticks * valorTick + CostoPorContrato();
            if (perdidaPorContrato <= 0) return 0;
            return Math.Min(MaxContratos, (int)Math.Floor(riesgoUSD / perdidaPorContrato + 1e-9));
        }

        private int TicksEntre(double a, double b)
        {
            return (int)Math.Round(Math.Abs(a - b) / TickSize);
        }

        private double StopATicks(double entrada, int ticks)
        {
            return Redondear(entrada - dirGrupo * Math.Max(1, ticks) * TickSize);
        }

        // Riesgo para cada operación: el riesgo máximo repartido entre las que pueden activarse,
        // sin pasar de lo que queda de la pérdida máxima diaria.
        private double RiesgoPorOperacion(int nOps)
        {
            int n = Math.Max(1, Math.Min(nOps, MaxOperacionesDiarias));
            double riesgo = RiesgoMaximoUSD / n;
            double cerradoHoy = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - pnlInicioDia;
            double restanteDia = PerdidaMaxDiariaUSD + cerradoHoy;
            return Math.Max(0, Math.Min(riesgo, restanteDia));
        }

        // Acerca el stop estructural a la entrada según ReduccionStopPct (30% = 70% de la distancia).
        // El objetivo no cambia. Siempre queda al menos 1 tick de distancia.
        private double StopReducido(double entrada, double stopEstructural)
        {
            double distancia = Math.Abs(entrada - stopEstructural) * (1.0 - ReduccionStopPct / 100.0);
            int ticks = Math.Max(1, (int)Math.Round(distancia / TickSize));
            int dir = stopEstructural < entrada ? 1 : -1;
            return Redondear(entrada - dir * ticks * TickSize);
        }

        // Contratos necesarios para ganar ProfitObjetivoUSD entre la entrada y el objetivo
        private int ContratosParaProfit(double entrada, double target)
        {
            double porContrato = Math.Abs(target - entrada) * Instrument.MasterInstrument.PointValue;
            if (porContrato <= 0) return 1;
            // Hacia arriba, para llegar al menos al profit objetivo (antes de comisiones)
            int qty = (int)Math.Ceiling(ProfitObjetivoUSD / porContrato - 1e-9);
            return Math.Max(1, Math.Min(MaxContratos, qty));
        }

        // Stop para perder como máximo riesgoUSD (con costos) sin cambiar contratos (operación ya colocada)
        private double StopParaRiesgoQtyFija(double entrada, int qty, double riesgoUSD)
        {
            return StopATicks(entrada, Math.Max(1, TicksMaxParaRiesgo(qty, riesgoUSD)));
        }

        // Operaciones abiertas o pendientes de llenarse
        private int OperacionesActivas()
        {
            int n = senalesAbiertas.Count;
            if (EstaViva(ordenPendingN1) && !senalesAbiertas.Contains(ordenPendingN1.Name)) n++;
            if (EstaViva(ordenPendingN2) && !senalesAbiertas.Contains(ordenPendingN2.Name)) n++;
            return n;
        }

        // Ajusta los stops activos a riesgoPorOp. Solo los acerca, nunca los aleja.
        private void ReajustarStops(double riesgoPorOp)
        {
            var senales = new HashSet<string>(senalesAbiertas);
            if (EstaViva(ordenPendingN1)) senales.Add(ordenPendingN1.Name);
            if (EstaViva(ordenPendingN2)) senales.Add(ordenPendingN2.Name);

            foreach (string senal in senales)
            {
                if (!qtyPorSenal.ContainsKey(senal) || !stopPorSenal.ContainsKey(senal)) continue;

                bool abierta = senalesAbiertas.Contains(senal);
                double entrada = abierta && entradaReal.ContainsKey(senal) ? entradaReal[senal]
                               : entradaPrevista.ContainsKey(senal) ? entradaPrevista[senal] : 0;
                if (entrada == 0) continue;

                double nuevoStop  = StopParaRiesgoQtyFija(entrada, qtyPorSenal[senal], riesgoPorOp);
                double stopActual = stopPorSenal[senal];
                bool masCerca = dirGrupo == 1 ? nuevoStop > stopActual : nuevoStop < stopActual;
                if (!masCerca) continue;

                // Si el nuevo stop ya quedó del otro lado del precio, se cierra a mercado
                bool superado = abierta && (dirGrupo == 1 ? nuevoStop >= Close[0] : nuevoStop <= Close[0]);
                if (superado)
                {
                    if (dirGrupo == 1) ExitLong("AjusteRiesgo", senal);
                    else               ExitShort("AjusteRiesgo", senal);
                    Print(string.Format("{0} {1} cerrada: el stop de ${2:0.00} ya estaba superado.", Time[0], senal, riesgoPorOp));
                    continue;
                }

                SetStopLoss(senal, CalculationMode.Price, nuevoStop, false);
                stopPorSenal[senal] = nuevoStop;
                Print(string.Format("{0} Stop de {1} ajustado {2} -> {3} (riesgo ${4:0.00}).", Time[0], senal, stopActual, nuevoStop, riesgoPorOp));
            }
        }

        // Guarda el plan de una operación y coloca su stop/objetivo
        private void RegistrarPlan(string senal, double entrada, double stop, double target, int qty)
        {
            SetStopLoss(senal, CalculationMode.Price, stop, false);
            SetProfitTarget(senal, CalculationMode.Price, target);
            entradaPrevista[senal] = entrada;
            stopPorSenal[senal]    = stop;
            targetPorSenal[senal]  = target;
            qtyPorSenal[senal]     = qty;

            double pv = Instrument.MasterInstrument.PointValue;
            Print(string.Format("{0} Plan {1}: {2} contratos | entrada {3} stop {4} objetivo {5} | +${6:0.00} / -${7:0.00}",
                Time[0], senal, qty, entrada, stop, target,
                Math.Abs(target - entrada) * pv * qty, Math.Abs(entrada - stop) * pv * qty));
        }

        // Solo se limpia sin posiciones abiertas, para no perder los datos de operaciones vivas
        private void LimpiarSeguimiento()
        {
            if (Position.MarketPosition != MarketPosition.Flat) return;
            entradaPrevista.Clear();
            entradaReal.Clear();
            stopPorSenal.Clear();
            targetPorSenal.Clear();
            qtyPorSenal.Clear();
            senalesAbiertas.Clear();
        }

        private static bool EstaViva(Order o)
        {
            return o != null && !Order.IsTerminalState(o.OrderState);
        }

        // Resultado del día: operaciones cerradas desde el reinicio diario + posición abierta
        private double PnlDelDia()
        {
            double cerrado = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - pnlInicioDia;
            double abierto = Position.MarketPosition == MarketPosition.Flat ? 0
                           : Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Closes[0][0]);
            return cerrado + abierto;
        }

        private DateTime ToCT(DateTime t)
        {
            return TimeZoneInfo.ConvertTime(t, Core.Globals.GeneralOptions.TimeZoneInfo, zonaCT);
        }

        private DateTime ToET(DateTime t)
        {
            return TimeZoneInfo.ConvertTime(t, Core.Globals.GeneralOptions.TimeZoneInfo, zonaET);
        }

        // ¿La hora (HHmmss) está dentro de [inicio, fin)? Admite rangos que cruzan medianoche.
        // Inicio = fin significa ventana desactivada.
        private bool EnRangoHorario(int hora, DateTime inicio, DateTime fin)
        {
            int ini = ToTime(inicio);
            int fn  = ToTime(fin);
            if (ini == fn) return false;
            if (ini < fn)  return hora >= ini && hora < fn;
            return hora >= ini || hora < fn;
        }

        private double Redondear(double precio)
        {
            return Instrument.MasterInstrument.RoundToTickSize(precio);
        }

        #endregion
    }
}
