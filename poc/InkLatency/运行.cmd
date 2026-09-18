@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ============================================================
echo   PoC-A 笔迹延迟三路线对比
echo ============================================================
echo.
echo   渲染路线：
echo     baseline  裸 InkCanvas（基线）
echo     a1        文档层自绘 + InkCanvas 湿笔迹叠加
echo     a2        单层自绘（湿笔迹也在 OnRender 里画）
echo.
echo   用法：在画布区域用笔 / 手指 / 鼠标连续书写 30 秒以上，
echo         切换三条路线各测一轮，最后点「导出 CSV」。
echo.
echo   CSV 落在程序同级的 data\latency\ 下。
echo.
pause
start "" "%~dp0bin\Release\net10.0-windows\InkLatency.exe" --mode a1
