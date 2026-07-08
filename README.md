# AutoDraw Unified (ColorSplitter & AutoDraw in Memory)

Una versión unificada y optimizada que fusiona las capacidades de segmentación de imágenes de **ColorSplitter** y la automatización de dibujado de **AutoDraw** en un único flujo continuo en memoria RAM, eliminando la necesidad de exportar e importar archivos PNG locales.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

## 🚀 Características Nuevas
- **Pipeline en Memoria:** Procesamiento directo desde la segmentación al motor de dibujo.
- **Orden Lógico (PathOptimizer):** Algoritmo de ordenamiento por luminancia y optimización de ruta del cursor (Nearest Neighbor) para trazos fluidos.
- **Interfaz Unificada:** Panel híbrido secuencial construido en Avalonia UI.

## 🛠️ Stack Tecnológico
- **Framework:** .NET 8.0 (Windows)
- **UI:** Avalonia UI 11.2.4
- **Imagen:** SkiaSharp 2.88.9, Magick.NET-Q8 13.10.0
- **Input:** SharpHook 5.3.8, SimWinMouse 1.0.3

## ⚙️ Controles
| Acción | Tecla por defecto |
|--------|-------------------|
| Iniciar dibujo | Left Shift |
| Detener dibujo | Left Alt |
| Pausar dibujo | Backslash |
| Saltar re-escaneo | Backspace |
| Bloquear preview | Left Control |
| Limpiar lock | Backtick |

## 📦 Compilación
```bash
dotnet build
dotnet run
```

## ⚖️ Créditos y Licencia
Este proyecto es una bifurcación/fusión (Fork/Mashup) de dos herramientas increíbles creadas por **Siydge** y **AlexDalas**:
- [ColorSplitter](https://github.com/auto-draw/ColorSplitter)
- [AutoDraw](https://github.com/auto-draw/autodraw)

Todo el código original de sus respectivos autores mantiene sus derechos intelectuales. Las modificaciones, la arquitectura de integración en memoria y el optimizador de ruta fueron implementados por **Harvey Rivas**.

Este proyecto se distribuye bajo la **Licencia MIT**. Consulta el archivo `LICENSE` para más detalles.
