# Resumen Técnico: TopstepBot

## 1. Arquitectura Multitemporal

El bot opera bajo un modelo jerárquico que procesa tres temporalidades simultáneamente para filtrar el ruido y alinear las entradas con la estructura institucional:

* **Temporalidad de 1 Hora (Contexto y Estructura):**
  * Evalúa el cierre de las velas y la posición del precio frente a las **EMA de 50 y 100 períodos**.
  * Detecta cambios de estructura institucionales (cuando el precio rompe intravela el máximo o mínimo previo).
  * Define una dirección operativa exclusiva: **Alcista (Compra)** o **Bajista (Venta)**. Si el contexto cambia con posiciones abiertas, ajusta automáticamente los objetivos de beneficio a un ratio conservador de **1:1**.

* **Temporalidad de 15 Minutos (Swings y Fibonacci):**
  * Identifica los retrocesos válidos (vela roja en tendencia alcista o vela verde en tendencia bajista).
  * Traza dinámicamente el *swing* desde el origen estructural hasta el extremo alcanzado.
  * Calcula y posiciona automáticamente dos órdenes límite en los niveles de retroceso de Fibonacci: **0.758** y **0.886**.

* **Temporalidad de 1 Minuto (Agregados Opcionales):**
  * Funciona como capa de ejecución fina. Si una entrada principal ya está activa, busca patrones de continuación y cruces de EMA en 1 minuto para sumar un micro-agregado con *stop* heredado.

---

## 2. Gestión de Órdenes, Niveles y Riesgo

* **Cálculo de Stops y Targets:**
  * **Stop Loss:** Se coloca a 2.0 puntos (8 ticks en MES) por detrás del nivel Fibonacci correspondiente (o del origen del swing para el nivel 0.886).
  * **Take Profit:** Se calcula aplicando un ratio fijo de recompensa/riesgo de 1:1.40 (`RATIO_RB = 1.40`) medido desde la distancia de entrada al *Stop Loss*.

* **Reglas Cruzadas entre 0.758 y 0.886:**
  * Si el nivel **0.758** se llena y alcanza su objetivo antes de que se active el **0.886**, la orden pendiente del 0.886 se cancela automáticamente.
  * Si el nivel **0.758** toca su *Stop Loss* mientras el **0.886** sigue pendiente, este último permanece activo hasta su propio objetivo o invalidación.

* **Invalidación del Fibonacci:** 
  * Si el precio rompe el mínimo absoluto de origen (en compras) o el máximo absoluto de origen (en ventas), el nivel de Fibonacci queda descartado permanentemente y se cancelan las órdenes pendientes.

---

## 3. Control de Exposición y Horarios (Reglas de Fondeo / Topstep)

* **Límite de Grupos Diarios:**
  * El sistema permite un máximo de **2 grupos de operaciones por día** (cada grupo comprende un Fibonacci con sus entradas principales y agregados). Un grupo se considera "consumido" únicamente al activarse la primera orden.

* **Cortes Horarios Obligatorios (Hora CT):**
  * **14:08 CT:** Se prohíbe la apertura de nuevos grupos operativos.
  * **15:08 CT:** Se envían órdenes de cancelación masiva para todas las órdenes límite pendientes en el *book*.
  * **15:10 CT:** Se fuerza el cierre por mercado de cualquier posición abierta (principales o agregados) para cumplir con las restricciones horarias de la firma de fondeo.