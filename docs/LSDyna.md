# LS-DYNA 数据接入与动画回放

目前已搭建数据转换、校验、Unity 回放和解析验证链路。工程原有 151 帧为 RAMMS 数据；本次尚无真实 LS-DYNA 滑坡结果。随工程提供的斜面滑块是解析测试数据，界面始终标注 `ANALYTIC VERIFICATION`。

## 在 Unity 中开始

使用工程指定的 Unity 2022.3.62f3c1，打开菜单 **Landslide → LS-DYNA → Open setup**。

1. 点击“打开独立解析验证场景”，按 Play，观察滑块沿 30° 斜面运动。可暂停、复位、拖动时间轴；到 5 秒停止。该场景验证接入链路，不代表山体滑坡求解。
2. 正式数据模式默认选中 `LS-DYNA 数据（严格）`，目录为 `StreamingAssets/LSDyna/Hunnedalen`。目前此目录没有真实结果，因此 Hunnedalen 桌面/VR 场景显示缺数据状态。
3. 获得求解结果后，按下文转换，再点击“导入已转换的数据目录…”并选择含 `dataset.json` 的目录。导入过程检查整套数据，将文件复制到工程独立目录并选中它。关闭“允许解析测试数据”。
4. 保存配置，重新进入 Hunnedalen 场景的 Play 模式。正式模式从首个求解时刻播放到最后时刻；原预警、RAMMS 时间线和人为后阶段不参与此模式。原 VR 播放、暂停、停止入口控制同一数据播放器。

若要查看原演示，在设置窗口显式选择 `原 RAMMS + 程序后阶段（非 LS-DYNA）`。其后阶段仍属于人为情景动画。

配置存于 `Assets/Resources/LSDynaPlaybackSettings.json`。设置在进入场景时读取；修改后需重新进入 Play。默认禁止把解析夹具当作正式结果使用。

## 从求解器输出接入

转换器位于 `Tools/LSDyna/convert_lsdyna.py`，仅依赖 Python 3.8+ 标准库。它接收经过明确整理的节点 CSV，**不直接解析 d3plot、nodout 或任意 LS-PrePost 原始导出格式，也不运行 LS-DYNA**。先用 LS-PrePost 或经验证的导出脚本整理为规定格式，保留原始求解文件和导出记录。

必需表头：

```csv
time,node_id,x,y,z,vx,vy,vz,mass
```

每组 `time` 包含相同节点集合；`x,y,z` 为变形后的绝对坐标；`vx,vy,vz` 为同一坐标系下的速度；`mass` 是有依据的节点集中质量。固定节点质量不应在帧间变化。`triangles.csv` 可选，表头为 `a,b,c`，使用源节点 ID 指定固定表面连接。没有连接时只显示节点标记，其尺寸不代表物理粒径或材料体积。

以下 PowerShell 示例假定源单位为 m、s、kg，源 Z 轴向上。替换路径、来源说明与原点，输出目录必须尚不存在：

```powershell
python Tools/LSDyna/convert_lsdyna.py `
  --input D:/Simulation/export/nodes.csv `
  --triangles D:/Simulation/export/triangles.csv `
  --output Assets/StreamingAssets/LSDyna/Hunnedalen `
  --source-description "LS-DYNA job名称、版本、模型与导出记录" `
  --length-to-m 1 --time-to-s 1 --mass-to-kg 1 `
  --source-origin 500000 6500000 100 `
  --axis-mapping x,z,y `
  --unity-offset 0 0 0
```

例如源长度 mm、时间 ms、质量 tonne，应分别使用 `0.001`、`0.001`、`1000`；实际参数必须依据该求解模型的一致单位表。大地坐标先用双精度减去固定原点，再存为 Unity float32，避免高程及小位移丢失。`unity-offset` 以米和 Unity 轴向给出，用于场景配准；播放器不进行自动贴地、缩放或路径修正。

完整字段、能量 CSV、变换公式及二进制协议见 [数据格式说明](LSDynaDataFormat.md)。真实滑坡求解所需的材料、孔压、重力平衡、接触、能量与收敛审查见 [物理建模与验证流程](LSDynaPhysicsWorkflow.md)。LS-DYNA 要求使用一致单位，可参考 [官方支持说明](https://www.dynasupport.com/howtos/general/consistent-units)。

## 校验与物理边界

转换生成 `dataset.json`、逐帧 `.bytes` 和 `validation.json`。报告包含质量、质心、动量、节点平动动能、重力势能、位置与速度梯形积分残差、float32 量化误差，以及可选的求解器能量诊断。所有诊断都不修改轨迹；通过格式检查不等于模型符合真实滑坡物理。

播放器严格检查来源标签、单位、节点编号、质量、递增时间、拓扑索引、帧长度、有限数值与逐帧 SHA-256。哈希保证包内一致性，不能证明原始 CSV 确由 LS-DYNA 生成。首次加载扫描所有帧，回放仅缓存相邻两帧；大型数据首次检查可能耗时，应在正式演示前完成检查。

位置与速度按各自导出值作分段线性插值。帧间显示速度不等于折线位置的导数；输出间隔越稀疏，漏掉冲击峰值或快速失稳过程的风险越高。播放倍速只改变观看时间，速度读数始终使用求解秒。播放范围外保持端点，没有额外重力、刚体碰撞、随机弹跳、强制沉积或外推。

当前只支持固定节点、固定质量及固定表面拓扑。单元侵蚀、删节点、自适应重网格和新断裂面需要扩展帧级拓扑后再接入；不能把失效前的表面连接继续拉伸来表示真实碎裂。只有表面节点时，报告的总质量和能量只代表这些节点；全滑体审计须涵盖其完整质量。

本地 Windows/Standalone 文件读取为已实现路径。Android 打包后的 `jar:` StreamingAssets 与网络 URL 会明确拒绝，需要先实现解压至本地的流程。未进行头显实机性能与立体交互验证。

## 可复现检查

Python：

```powershell
python -m unittest discover -s Tools/LSDyna/tests -v
```

解析夹具已放在 `Assets/StreamingAssets/LSDyna/Verification`，包含生成它的 CSV 与参数。重新生成时请选择新目录：

```powershell
python Tools/LSDyna/convert_lsdyna.py --generate-verification --output Validation/NewVerification
```

Unity 批处理入口为 `LsDynaVerificationChecks.RunBatch`；请使用默认严格配置、保留图形设备、不添加 `-quit`，由检查程序自行退出。报告和 0/2/5 秒预览图写入 `Validation/LSDyna`。检查覆盖解析解、真实时间插值、暂停/复位、末帧保持、文件损坏和原 Hunnedalen 场景的缺数据保护。它验证接入与播放，不替代真实 LS-DYNA 模型验证。
