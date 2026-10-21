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
	public class TopstepBot : Strategy
	{
		// Variables de control de contexto y grupos diarios
		private int gruposHoy = 0;
		private DateTime fechaActual = DateTime.MinValue;
		
		// Indicadores para el contexto de 1 Hora (se añadirán como DataSeries adicionales)
		private SMA ema50_1H;
		private SMA ema100_1H;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Estrategia de retrocesos Fibonacci multitemporal para Topstep";
				Name										= "TopstepBot";
				Calculate									= Calculate.OnBarClose;
				EntriesPerDirection							= 2;
				EntryHandling								= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy				= true;
				ExitOnSessionCloseSeconds					= 30;
				IsFillLimitOnTouch							= true;
				MaximumBarsLookBack							= MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution							= OrderFillResolution.Standard;
				Slippage									= 0;
				StartBehavior								= StartBehavior.WaitUntilFlat;
				TimeInForce									= TimeInForce.Gtc;
				TraceOrders									= true;
				RealtimeErrorHandling						= RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling							= StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade							= 100;
			}
			else if (State == State.Configure)
			{
				// Añadimos la temporalidad de 1 Hora (BarsPeriod 1)
				AddDataSeries(BarsPeriodType.Minute, 60);
				
				// Añadimos la temporalidad de 15 Minutos (BarsPeriod 2)
				AddDataSeries(BarsPeriodType.Minute, 15);
			}
			else if (State == State.DataLoaded)
			{
				// Inicializamos las EMAs sobre el gráfico de 1 Hora (Index 1)
				ema50_1H = SMA(BarsArray[1], 50);
				ema100_1H = SMA(BarsArray[1], 100);
			}
		}

		protected override void OnBarUpdate()
		{
			// Validar que tengamos datos suficientes en todas las temporalidades
			if (CurrentBars[0] < 100 || CurrentBars[1] < 100 || CurrentBars[2] < 100)
				return;

			// Control de cambio de día para reiniciar el contador de grupos diarios (máximo 2 por día)
			if (Time[0].Date != fechaActual)
			{
				fechaActual = Time[0].Date;
				gruposHoy = 0;
			}

			// Corte obligatorio por horario de Topstep (15:08 CT cancela pendientes, 15:10 CT cierra todo)
			int horaActual = ToTime(Time[0]);
			if (horaActual >= 150800)
			{
				ExitLong();
				ExitShort();
				CancelPendingOrders();
				return;
			}

			// Solo evaluamos lógica principal en la barra que cierra en la temporalidad de 15 minutos (BarsArray[2])
			if (BarsInProgress != 2)
				return;

			// Límite de grupos diarios alcanzado
			if (gruposHoy >= 2)
				return;

			// --- AQUÍ SE IMPLEMENTARÁ LA LÓGICA DE CONTEXTO 1H Y TRAZADO DE FIBONACCI EN 15M ---
			// Ejemplo conceptual para evaluar el contexto de 1H (BarsArray[1]):
			double precio1H = Closes[1][0];
			bool contextoAlcista = (precio1H > ema50_1H[0] && precio1H > ema100_1H[0]);
			bool contextoBajista = (precio1H < ema50_1H[0] && precio1H < ema100_1H[0]);

			if (contextoAlcista && Position.MarketPosition == MarketPosition.Flat)
			{
				// Aquí calculamos los niveles de Fibonacci (0.758 y 0.886) basados en el último swing de 15m
				// Y enviamos las órdenes Limit con sus respectivos Stops y Targets (Ratio 1:1.40)
				
				// Ejemplo de orden límite para simular el nivel:
				// double nivel0758 = ...; 
				// EnterLongLimit(0, true, 1, nivel0758, "Entrada_0758");
			}
		}
	}
}