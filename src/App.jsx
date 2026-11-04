import G1 from './components/g1.jsx'
import G2 from './components/g2.jsx'
import { NavLink, Route, Routes } from 'react-router-dom'
const App = () => {
  return <>
    <nav className="chart-navigator" aria-label="Navegación de gráficos">
      <NavLink to="/" end className={({ isActive }) => isActive ? "active" : ""}>
        SPY
      </NavLink>
      <NavLink to="/g2" className={({ isActive }) => isActive ? "active" : ""}>
        S&amp;P 500
      </NavLink>
    </nav>
    <Routes>
      <Route path="/" element={<G1 />} />
      <Route path="/g2" element={<G2 />} />
    </Routes>
  </>
}
export default App
