# LS-DYNA 接入验证记录

2026-10-26，本机 Unity 2022.3.62f3c1，Windows。

- Python 转换器：15 项单元测试通过，见 [python-checks.txt](python-checks.txt)。
- Unity 全工程批处理编译退出码 0。
- Unity 数据、播放和原 Hunnedalen 桌面场景检查退出码 0，见 [unity-checks.txt](unity-checks.txt)。最终严格模式启动无 managed exception。缺少真实结果的提示是预期行为。
- 目视检查 `analytic-t0.png`、`analytic-t5.png`：滑块沿参考斜面移动，速度色从低速蓝色变为高速红色，几何没有明显错位。另有 `analytic-t2.png`。

解析验证模型：30° 无限平面、刚性滑块、μ=0.3、g=9.81 m/s²、0–5 s，8 节点、51 帧。`a=g(sinθ−μcosθ)=2.3562872367 m/s²`。转换后最大位置/速度梯形积分残差约 `1.38329×10⁻⁶ m`；机械能损失与解析摩擦功相差约 `0.013 J`，来自存储量化等数值误差。

这些记录证明接入与解析测试链路可运行，不代表已运行或验证 Hunnedalen 的 LS-DYNA 山体滑坡模型。尚无真实 LS-DYNA 结果；单元侵蚀、变化拓扑和头显实机性能未覆盖。现有工程第三方资源/过时接口以及无头显情况下的 XR 插件仍有警告，详见完整日志。
