import { useEffect, useRef, useState } from "react"

const colors = {
  background: "#11151b",
  grid: "#252b34",
  muted: "#778190",
  green: "#22c58b",
  red: "#f05d68",
}

const initialVisibleCount = (candles) => Math.min(20, candles.length)
const formatPrice = (price) => price.toFixed(2)
const formatVolume = (volume) => new Intl.NumberFormat("en-US").format(volume)

const formatCandleDate = (datetime, interval) => {
  if (interval === "1min") return datetime.slice(11, 16)
  return new Intl.DateTimeFormat("es-US", {
    month: "short",
    day: "numeric",
    timeZone: "UTC",
  }).format(new Date(`${datetime.slice(0, 10)}T00:00:00Z`))
}

function drawChart(canvas, candles, activeIndex, visibleCount, visibleEnd, interval) {
  const rect = canvas.getBoundingClientRect()
  if (!rect.width || !rect.height || !candles.length) return

  const endIndex = Math.min(candles.length, Math.max(visibleCount, visibleEnd))
  const firstVisibleIndex = endIndex - visibleCount
  const visibleCandles = candles.slice(firstVisibleIndex, endIndex)
  const activeVisibleIndex = Math.max(
    0,
    Math.min(visibleCandles.length - 1, activeIndex - firstVisibleIndex),
  )
  const dpr = window.devicePixelRatio || 1
  canvas.width = Math.round(rect.width * dpr)
  canvas.height = Math.round(rect.height * dpr)
  const ctx = canvas.getContext("2d")
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)

  const width = rect.width
  const height = rect.height
  const left = 18
  const right = 78
  const top = 20
  const bottom = 34
  const volumeHeight = Math.min(110, height * 0.22)
  const volumeGap = 18
  const priceBottom = height - bottom - volumeHeight - volumeGap
  const plotWidth = width - left - right
  const plotHeight = priceBottom - top
  const minPrice = Math.min(...visibleCandles.map(({ low }) => low))
  const maxPrice = Math.max(...visibleCandles.map(({ high }) => high))
  const pricePadding = (maxPrice - minPrice) * 0.12 || 0.1
  const priceMin = minPrice - pricePadding
  const priceMax = maxPrice + pricePadding
  const maxVolume = Math.max(...visibleCandles.map(({ volume }) => volume), 1)
  const step = plotWidth / visibleCandles.length
  const bodyWidth = Math.min(36, step * 0.62)
  const priceY = (price) =>
    top + ((priceMax - price) / (priceMax - priceMin)) * plotHeight
  const candleX = (index) => left + step * (index + 0.5)

  ctx.fillStyle = colors.background
  ctx.fillRect(0, 0, width, height)
  ctx.font = "12px system-ui, sans-serif"
  ctx.textBaseline = "middle"

  for (let line = 0; line <= 5; line += 1) {
    const y = top + (plotHeight / 5) * line
    const value = priceMax - ((priceMax - priceMin) / 5) * line
    ctx.strokeStyle = colors.grid
    ctx.lineWidth = 1
    ctx.beginPath()
    ctx.moveTo(left, y)
    ctx.lineTo(width - right + 6, y)
    ctx.stroke()
    ctx.fillStyle = colors.muted
    ctx.textAlign = "left"
    ctx.fillText(formatPrice(value), width - right + 14, y)
  }

  ctx.strokeStyle = colors.grid
  ctx.beginPath()
  ctx.moveTo(left, priceBottom + volumeGap / 2)
  ctx.lineTo(width - right + 6, priceBottom + volumeGap / 2)
  ctx.stroke()

  const candle = visibleCandles[activeVisibleIndex]
  const activeColor = candle.close >= candle.open ? colors.green : colors.red
  const activeX = candleX(activeVisibleIndex)
  const activePriceY = priceY(candle.close)
  ctx.save()
  ctx.setLineDash([4, 5])
  ctx.strokeStyle = "#596575"
  ctx.beginPath()
  ctx.moveTo(activeX, top)
  ctx.lineTo(activeX, height - bottom)
  ctx.moveTo(left, activePriceY)
  ctx.lineTo(width - right + 6, activePriceY)
  ctx.stroke()
  ctx.restore()

  visibleCandles.forEach((item, index) => {
    const x = candleX(index)
    const color = item.close >= item.open ? colors.green : colors.red
    const highY = priceY(item.high)
    const lowY = priceY(item.low)
    const openY = priceY(item.open)
    const closeY = priceY(item.close)
    const bodyTop = Math.min(openY, closeY)
    const bodyHeight = Math.max(Math.abs(closeY - openY), 1.5)
    const volumeBarHeight = (item.volume / maxVolume) * volumeHeight

    ctx.strokeStyle = color
    ctx.lineWidth = 1.5
    ctx.beginPath()
    ctx.moveTo(x, highY)
    ctx.lineTo(x, lowY)
    ctx.stroke()

    ctx.fillStyle = color
    ctx.fillRect(x - bodyWidth / 2, bodyTop, bodyWidth, bodyHeight)
    ctx.globalAlpha = 0.58
    ctx.fillRect(
      x - bodyWidth / 2,
      height - bottom - volumeBarHeight,
      bodyWidth,
      volumeBarHeight,
    )
    ctx.globalAlpha = 1
  })

  ctx.fillStyle = activeColor
  ctx.fillRect(width - right + 8, activePriceY - 11, right - 12, 22)
  ctx.fillStyle = "#0d1218"
  ctx.textAlign = "left"
  ctx.font = "bold 12px system-ui, sans-serif"
  ctx.fillText(formatPrice(candle.close), width - right + 14, activePriceY)

  ctx.fillStyle = colors.muted
  ctx.font = "11px system-ui, sans-serif"
  ctx.textAlign = "center"
  const labelCount = Math.min(
    visibleCandles.length,
    Math.max(2, Math.floor(plotWidth / 100)),
  )
  for (let label = 0; label < labelCount; label += 1) {
    const index = Math.round(
      (label * (visibleCandles.length - 1)) / (labelCount - 1),
    )
    ctx.fillText(
      formatCandleDate(visibleCandles[index].datetime, interval),
      candleX(index),
      height - 15,
    )
  }

  ctx.textAlign = "left"
  ctx.fillText("VOLUMEN", left, priceBottom + volumeGap + 12)
}

function MarketChart({ candles, instrument }) {
  const canvasRef = useRef(null)
  const dragRef = useRef(null)
  const [activeIndex, setActiveIndex] = useState(candles.length - 1)
  const [visibleCount, setVisibleCount] = useState(() =>
    initialVisibleCount(candles),
  )
  const [visibleEnd, setVisibleEnd] = useState(candles.length)
  const [isDragging, setIsDragging] = useState(false)
  const candle = candles[activeIndex]
  const previousCandle = candles[Math.max(activeIndex - 1, 0)]
  const change = candle.close - previousCandle.close
  const changePercent = previousCandle.close
    ? (change / previousCandle.close) * 100
    : 0
  const isPositive = candle.close >= candle.open
  const instrumentName = instrument.name || instrument.type
  const displayDate = instrument.interval === "1min"
    ? candle.datetime.replace(" ", " · ")
    : new Intl.DateTimeFormat("es-US", {
      dateStyle: "medium",
      timeZone: "UTC",
    }).format(new Date(`${candle.datetime.slice(0, 10)}T00:00:00Z`))

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return undefined

    const redraw = () =>
      drawChart(canvas, candles, activeIndex, visibleCount, visibleEnd, instrument.interval)
    const resizeObserver = new ResizeObserver(redraw)
    resizeObserver.observe(canvas)
    redraw()

    return () => resizeObserver.disconnect()
  }, [activeIndex, candles, instrument.interval, visibleCount, visibleEnd])

  const handlePointerMove = (event) => {
    const canvas = canvasRef.current
    if (!canvas) return
    const rect = canvas.getBoundingClientRect()
    const left = 18
    const right = 78
    const plotWidth = rect.width - left - right
    const position = event.clientX - rect.left - left
    let endIndex = visibleEnd

    if (dragRef.current?.pointerId === event.pointerId) {
      const dragDistance = event.clientX - dragRef.current.startX
      const candlesMoved = Math.round((dragDistance / plotWidth) * visibleCount)
      endIndex = Math.max(
        visibleCount,
        Math.min(candles.length, dragRef.current.startEnd - candlesMoved),
      )
      setVisibleEnd(endIndex)
    }

    const firstVisibleIndex = endIndex - visibleCount
    const visibleCandles = candles.slice(firstVisibleIndex, endIndex)
    const visibleIndex = Math.floor((position / plotWidth) * visibleCandles.length)
    const index = firstVisibleIndex + visibleIndex
    setActiveIndex(
      Math.max(firstVisibleIndex, Math.min(endIndex - 1, index)),
    )
  }

  const handlePointerDown = (event) => {
    if (event.button !== 0) return
    const canvas = canvasRef.current
    if (!canvas) return
    dragRef.current = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startEnd: visibleEnd,
    }
    canvas.setPointerCapture(event.pointerId)
    setIsDragging(true)
  }

  const handlePointerUp = (event) => {
    if (dragRef.current?.pointerId !== event.pointerId) return
    dragRef.current = null
    setIsDragging(false)
    if (canvasRef.current?.hasPointerCapture(event.pointerId)) {
      canvasRef.current.releasePointerCapture(event.pointerId)
    }
  }

  const handleWheel = (event) => {
    event.preventDefault()
    const zoomStep = Math.max(1, Math.ceil(visibleCount * 0.12))
    const nextCount = Math.max(
      Math.min(5, candles.length),
      Math.min(candles.length, visibleCount + (event.deltaY > 0 ? zoomStep : -zoomStep)),
    )
    const nextEnd = Math.max(nextCount, visibleEnd)
    setVisibleCount(nextCount)
    setVisibleEnd(nextEnd)
    setActiveIndex((current) =>
      Math.max(nextEnd - nextCount, Math.min(nextEnd - 1, current)),
    )
  }

  return (
    <main className="market-page">
      <section className="chart-card" aria-label={`Gráfico de ${instrument.symbol}`}>
        <header className="chart-header">
          <div className="instrument">
            <div className="instrument-icon" aria-hidden="true">
              {instrument.symbol.slice(0, 1).replace("^", "S")}
            </div>
            <div>
              <div className="instrument-title">
                <h1>{instrument.symbol.replace("^", "")}</h1>
                <span className="market-badge">{instrument.exchange}</span>
              </div>
              <p>{instrumentName} · {instrument.interval}</p>
            </div>
          </div>
          <div className="session-badge"><span /> Datos de mercado</div>
        </header>

        <div className="quote-row">
          <div className="quote-main">
            <span className="quote-price">{formatPrice(candle.close)}</span>
            <span className={`quote-change ${isPositive ? "positive" : "negative"}`}>
              {change >= 0 ? "+" : ""}{change.toFixed(2)} ({changePercent >= 0 ? "+" : ""}{changePercent.toFixed(2)}%)
            </span>
            <span className="quote-currency">{instrument.currency}</span>
          </div>
          <div className="quote-date">{displayDate}</div>
        </div>

        <div className="ohlc-strip" aria-live="polite">
          <span>O <strong>{formatPrice(candle.open)}</strong></span>
          <span>H <strong>{formatPrice(candle.high)}</strong></span>
          <span>L <strong>{formatPrice(candle.low)}</strong></span>
          <span>C <strong>{formatPrice(candle.close)}</strong></span>
          <span>Vol <strong>{formatVolume(candle.volume)}</strong></span>
        </div>

        <div className="chart-wrap">
          <canvas
            ref={canvasRef}
            className={isDragging ? "is-dragging" : ""}
            aria-label={`Velas de ${instrument.symbol}. OHLC: apertura ${formatPrice(candle.open)}, máximo ${formatPrice(candle.high)}, mínimo ${formatPrice(candle.low)}, cierre ${formatPrice(candle.close)}, volumen ${formatVolume(candle.volume)}.`}
            onPointerMove={handlePointerMove}
            onPointerDown={handlePointerDown}
            onPointerUp={handlePointerUp}
            onPointerCancel={handlePointerUp}
            onLostPointerCapture={handlePointerUp}
            onWheel={handleWheel}
          />
        </div>
        <footer className="chart-footer">
          <span><i className="legend-dot up" /> Alcista</span>
          <span><i className="legend-dot down" /> Bajista</span>
          <span className="chart-hint">{visibleCount} velas · Arrastra para desplazarte · Rueda para zoom</span>
        </footer>
      </section>
    </main>
  )
}

export default MarketChart
