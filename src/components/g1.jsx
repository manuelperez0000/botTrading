import velas from "../velas/velas.json"
import MarketChart from "./MarketChart.jsx"

const candles = [...velas.values]
  .map((candle) => ({
    ...candle,
    open: Number(candle.open),
    high: Number(candle.high),
    low: Number(candle.low),
    close: Number(candle.close),
    volume: Number(candle.volume),
  }))
  .sort((a, b) => a.datetime.localeCompare(b.datetime))

const G1 = () => (
  <MarketChart
    candles={candles}
    instrument={{
      symbol: velas.meta.symbol,
      exchange: velas.meta.exchange,
      type: velas.meta.type,
      interval: velas.meta.interval,
      currency: velas.meta.currency,
    }}
  />
)

export default G1
