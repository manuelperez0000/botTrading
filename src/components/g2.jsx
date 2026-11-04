import datosSP500 from "../velas/sp500.json"
import MarketChart from "./MarketChart.jsx"

const result = datosSP500.chart.result[0]
const timestamps = result.timestamp || []
const quote = result.indicators.quote[0]
const closeValues =
  quote.close || result.indicators.adjclose?.[0]?.adjclose || []
const seriesValue = (series, index) => {
  const seriesIndex = series.length - timestamps.length + index
  const value = seriesIndex >= 0 ? series[seriesIndex] : null
  return value == null ? Number.NaN : Number(value)
}

const candles = timestamps
  .map((timestamp, index) => {
    if (!Number.isFinite(timestamp)) return null
    const close = seriesValue(closeValues, index)
    if (!Number.isFinite(close)) return null

    const rawPreviousClose = index > 0
      ? seriesValue(closeValues, index - 1)
      : close
    const previousClose = Number.isFinite(rawPreviousClose) ? rawPreviousClose : close
    const rawOpen = seriesValue(quote.open || [], index)
    const open = Number.isFinite(rawOpen) && Math.abs(rawOpen - close) / close < 0.2
      ? rawOpen
      : previousClose
    const rawHigh = seriesValue(quote.high || [], index)
    const rawLow = seriesValue(quote.low || [], index)
    const fallbackHigh = Math.max(open, close)
    const fallbackLow = Math.min(open, close)
    const high = Number.isFinite(rawHigh) &&
      rawHigh >= fallbackHigh &&
      (rawHigh - close) / close < 0.2
      ? rawHigh
      : fallbackHigh
    const low = Number.isFinite(rawLow) &&
      rawLow <= fallbackLow &&
      (close - rawLow) / close < 0.2
      ? rawLow
      : fallbackLow
    const volume = seriesValue(quote.volume || [], index)

    return {
      datetime: new Date(timestamp * 1000).toISOString().slice(0, 10),
      open,
      high,
      low,
      close,
      volume: Number.isFinite(volume) ? volume : 0,
    }
  })
  .filter(Boolean)

const instrument = {
  symbol: result.meta.symbol,
  name: result.meta.longName,
  exchange: result.meta.exchangeName,
  type: result.meta.instrumentType,
  interval: result.meta.dataGranularity,
  currency: result.meta.currency,
}

const G2 = () => <MarketChart candles={candles} instrument={instrument} />

export default G2
