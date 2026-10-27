# LS-DYNA 结果接入与物理核查

本流程将**已经求解、已经导出的节点时序**转换成 Unity 动画。它不运行 LS-DYNA，也不直接解析 `d3plot`、`binout`、`nodout` 或任意版本的 LS-PrePost 原始 CSV。

若模型由降雨触发，可先按[降雨场地准备说明](RainfallCasePreparation.md)建立有来源的三维地形、土层与雨强输入，并审计外部水文和稳定性结果。转换时可用 `--site-case-report` 附加其 `case_report.json`；报告只证明准备文件与数据包的关联及完整性，不证明渗流、孔压或滑坡求解正确。

本仓库原有 `SceneData/Hunnedalen/Frames/Frame_*.bin` 来自 RAMMS，不能重新命名为 LS-DYNA 结果。随项目提供的 `Assets/StreamingAssets/LSDyna/Verification` 是**解析斜面滑块验证算例**，标记为 `ANALYTIC / analytic-verification`，不能当作 Hunnedalen 滑坡预测或 LS-DYNA 求解成果。

## 从实际求解到动画

1. 保存实际 LS-DYNA 输入模型、求解器版本和作业标识。保留材料参数、初始应力、重力、边界条件、接触/摩擦、孔隙水压力、侵蚀/失效、网格与时间步控制的依据。
2. 用对应版本的 LS-PrePost 或已有后处理脚本，从结果文件提取节点 ID、真实输出时间、**变形后绝对坐标**、同坐标系的平动速度和节点质量。若拿到的是位移，先用 `初始坐标 + 位移` 得到绝对坐标；不能把位移直接放进 `x,y,z`。
3. 将数据整理为下述固定列格式。此规范化步骤由结果提供者完成；各版本的导出布局不同，本工具不会猜测列、坐标轴或单位。
4. 明确单位、原点和坐标变换，运行转换器。阅读 `validation.json`，核查位置/速度一致性及能量、动量趋势，再在 Unity 加载数据。

节点质量应来自该计算模型的质量分配，不能用一个随意的“岩块质量”替代。若只导出表面节点，报告中的总质量和能量仅对应所导出节点；若要计算整个滑体的质心、动量和节点平动动能，应导出整个滑体的质量节点，三角面仍可只引用表面节点。若使用质量缩放，应记录实际求解质量与附加质量。

## 规范化节点 CSV

编码为 UTF-8（可带 BOM），逗号分隔、小数点为 `.`。表头必须恰好包含以下九列；允许调整列顺序，不允许重复列或额外列。

```csv
time,node_id,x,y,z,vx,vy,vz,mass
0,101,500000,6500000,100,0,0,0,1250
0,102,500002,6500000,100,0,0,0,1250
0.1,101,500000.01,6500000,99.995,0.2,0,-0.1,1250
0.1,102,500002.01,6500000,99.995,0.2,0,-0.1,1250
```

此片段仅示范格式，不是实际求解数据。

| 列 | 含义与限制 |
|---|---|
| `time` | 求解器真实输出时间；同一帧连续排列，各帧严格递增，允许不等间隔和负起始时间 |
| `node_id` | 1–2147483647 的整数 ID；同帧不能重复，各帧节点集合必须相同 |
| `x,y,z` | 当前帧变形后的绝对坐标，原始模型长度单位 |
| `vx,vy,vz` | 原始模型坐标轴中的速度，长度单位/时间单位 |
| `mass` | 节点质量，原始模型质量单位；必须大于零，并在全部帧保持相同 |

每帧内部允许节点乱序，转换器按节点 ID 升序写出。单帧可用于静态显示，但不能核查时间一致性。拒绝 NaN、Infinity、缺列、空单元、重复节点、时间回退、不同节点集合、质量变化、float32 溢出及质量下溢为零。

当前 schema 不支持节点增删、侵蚀后节点消失、重网格、粒子出生死亡或逐帧变化的拓扑。如果实际分析包含这些现象，需要扩展协议并保留活跃状态/质量变化信息；不能补零或随意复制节点来绕过检查。

## 可选表面网格

`triangles.csv` 的三列为原始节点 ID，不是数组下标：

```csv
a,b,c
101,102,103
101,103,104
```

三角面必须来自原始模型表面拓扑。拒绝未知节点、三点中 ID 重复、重复三角面和空拓扑文件。不提供该文件时，manifest 的 `triangles` 为空。不能用连线把本来不相邻的碎块拼成连续表面。

输入绕序应与原模型外法向一致。若坐标映射的行列式为负，转换器交换每个三角形的第二、第三个索引；Unity 读取时不应再翻一次。几何退化、三角形翻转、壳厚度、体单元内部面剔除等仍需在后处理中检查。

## 单位与坐标

LS-DYNA 要求一致的单位体系，不能从数值或文件扩展名可靠地推断。转换必须显式提供 `--length-to-m`、`--time-to-s`、`--mass-to-kg` 三个正数。单位关系见 [LS-DYNA 官方支持：一致单位](https://www.dynasupport.com/howtos/general/consistent-units)。

转换顺序为：

```text
p_unity = axisMap((p_source - sourceOrigin) × lengthToM) + unityOffsetM
v_unity = axisMap(v_source × lengthToM / timeToS)
t_seconds = t_source × timeToS
m_kg = m_source × massToKg
```

先用双精度减去测量/地理原点，再写 float32 坐标，避免百万米坐标吞掉毫米级位移。`--source-origin` 使用**源坐标和源长度单位**，必须显式指定；已经是局部坐标时写 `0 0 0`。`--unity-offset` 使用 Unity 轴顺序和米，不影响速度。

`--axis-mapping x,z,y` 表示 Unity 的 X、Y、Z 分别取源 x、z、y，适用于源 Z 向上的常见情况；这不是对所有 LS-DYNA 模型的自动判断。可指定带符号的排列，例如 `--axis-mapping=-x,z,y`。其他旋转或地理投影应先在规范化导出阶段完成，并记录在来源说明中。

播放数据已包含上述变换。保持回放对象的比例为 `(1,1,1)`，避免再次缩放速度、尺寸或移动原点。诊断重力默认是 Unity 坐标 `(0,-9.81,0)` m/s²；模型方向不同时显式设置 `--gravity`，否则势能诊断没有对应的物理意义。

## 转换命令

只需 Python 3.8 或更新版本的标准库。从 `Unity_Project` 目录运行。以下为 PowerShell 单行示例，路径和单位需替换成实际数据：

```powershell
python Tools/LSDyna/convert_lsdyna.py --input D:/SimulationExport/nodes.csv --triangles D:/SimulationExport/triangles.csv --energy D:/SimulationExport/energy.csv --output Assets/StreamingAssets/LSDyna/HunnedalenRun01 --source-description "LS-DYNA Rxx; job HunnedalenRun01; export by LS-PrePost version xx; units m-s-kg; model revision xx" --length-to-m 1 --time-to-s 1 --mass-to-kg 1 --source-origin 500000 6500000 100 --axis-mapping x,z,y --unity-offset 0 0 0
```

没有三角网格或能量文件时，省略对应的 `--triangles` 或 `--energy`。来源说明不能为空；应填写真实作业、版本、导出方式及模型标识。工具中的 `LS-DYNA` 标签表示提供者声明的来源，SHA-256 证明文件一致性，不证明它确实经过求解。

若已经用场地准备工具生成 `case_report.json`，可在真实结果转换命令末尾添加 `--site-case-report D:/Survey/prepared-case/case_report.json`。转换器复制该报告，并在 manifest 中记录案例 ID、缺项数和哈希；Unity 导入与加载时复核报告完整性。报告状态始终为 `prepared-not-solved`，物理可信度仍须独立验证。解析斜面夹具不接受场地报告。

`--output` 必须是尚不存在的目录。转换完成全部验证后才发布整套结果；失败时不会留下半套目标数据，也不会覆盖现有结果。需要修订结果时使用新的目录名。

生成一个新的解析验证副本：

```powershell
python Tools/LSDyna/convert_lsdyna.py --generate-verification --output Tools/LSDyna/fixtures/VerificationCopy
```

运行测试：

```powershell
python -B -m unittest discover -s Tools/LSDyna/tests -v
```

## 输出协议 schemaVersion 1

结果目录包含 `dataset.json`、`validation.json` 和按时间排列的 `frame_000000.bytes` 等帧文件。当前转换器与 Unity 读取器限制一致：最多 **200000 节点、100000 帧、3000000 个三角索引（1000000 面）**，`dataset.json` 最多 **32 MiB**。这些是接入上限，不代表任意设备可以实时播放达到上限的数据。

`dataset.json` 的核心字段：

| 字段 | 内容 |
|---|---|
| `schemaVersion` | `1` |
| `solver` / `provenanceKind` | 真实声明导出为 `LS-DYNA` / `solver-export`；验证夹具为 `ANALYTIC` / `analytic-verification` |
| `sourceDescription` | 非空来源说明 |
| `sourceSha256` | 规范化节点 CSV 的 SHA-256；`sourceHashes` 另记录可选输入文件 |
| `coordinateConvention` / `timeUnit` | `Unity-local-m` / `s` |
| `nodeIds` | 全部节点 ID，顺序对应帧内记录 |
| `massesKg` | 对应节点的正质量，单位 kg，跨帧恒定 |
| `triangles` | 扁平三角索引数组，**零起始数组下标**，不是节点 ID |
| `timesSeconds` | 双精度的真实秒数，严格递增，不假设帧间隔相同 |
| `frameFiles` | 相对于本目录的逐帧文件名 |
| `frameSha256` | 每个帧文件的 SHA-256，与 `frameFiles` 一一对应，Unity 读取时必填 |
| `validationSummary` | 转换器检查摘要，不是物理可信度认证 |
| `unitConversion` / `sourceOrigin` / `axisMapping` / `unityOffsetM` | 可追溯的变换参数 |
| `gravityMPerS2` | 物理诊断使用的 Unity 重力向量 |
| `siteCaseReport`（可选） | 降雨场地报告的文件名、SHA-256、案例 ID、缺项数和 `prepared-not-solved` 状态；不是验证通过标志 |

每个 `.bytes` 文件全部采用小端序，长度必须等于 `12 + 24 × 节点数` 字节：

| 偏移 | 类型 | 内容 |
|---|---|---|
| 0 | 8 字节 ASCII | `LSDYNA01` |
| 8 | int32 | 节点数 |
| 12 起 | 每节点 6 × float32 | `x,y,z,vx,vy,vz`，单位 m、m/s |

Unity 的首次扫描和每次读帧都校验帧结构、数值及哈希。这个过程检测损坏或协议错误；不会认证材料、接触或真实滑坡物理。

## 物理诊断与能量 CSV

`validation.json` 对**实际写入 float32 的回放值**计算每帧总质量、质心、线动量、平动动能和重力势能：

```text
M = Σm_i
r_cm = Σ(m_i r_i)/M
P = Σ(m_i v_i)
K = 1/2 Σ(m_i |v_i|²)
U_g = -Σ(m_i g·r_i)
```

势能零点由原点选择决定；比较变化量才有物理意义。节点平动动能不含未导出的转动自由度，也不代替求解器全局能量。存在摩擦、阻尼、碰撞和破坏时，`K + U_g` 通常会变化，不能强行保持常数。

相邻输出帧计算梯形积分残差：

```text
e_i = r_i(t1) - r_i(t0) - 0.5 × [v_i(t0) + v_i(t1)] × (t1-t0)
```

报告最大值、均方根和最差节点。默认超过 `0.05 m` 发出警告，可用 `--residual-warning-m` 调整。这是位置、速度、时间和单位的一致性提示；粗输出间隔和急剧加速度变化也会产生残差。残差小不代表动力平衡、稳定性或材料模型正确。转换器不会修改轨迹、速度或质量来“修正”残差。

可选 `energy.csv` 为人工整理后的求解器能量历史，表头必须恰好为：

```csv
time,kinetic_energy,internal_energy,external_work,hourglass_energy
0,0,0,0,0
0.1,10,5,15,0.1
```

该片段只是字段示例。所有能量使用与节点数据一致的 `质量 × 长度² / 时间²` 单位；工具转换成 J。时间严格递增，能量记录可以使用与节点数据不同的输出步长；当前工具只分别报告，不自动配准或宣称两者一致。动能必须非负；其他能量保留求解器符号约定。记录 `Δ(K+内能)-Δ外功` 和 `|沙漏能/内能|`，内能接近零时比值为 `null`。

实际能量收支必须结合求解器输出选项审阅接触、阻尼、沙漏、侵蚀、重力外功和质量缩放。不同输出渠道及控制选项的记账方式可能不同，因此工具单列沙漏能，不机械地再加一遍。参见 [LS-DYNA 官方支持：Energy data](https://www.dynasupport.com/tutorial/ls-dyna-users-guide/energy-data) 与 [Total energy](https://www.dynasupport.com/howtos/general/total-energy)。本报告始终标为 `diagnostic-only`，没有“守恒自动验证通过”结论。

## 随附解析算例的确切含义

验证夹具采用无限长 30° 平面、刚性滑块、恒定库仑动摩擦 `μ=0.3`、重力 `g=9.81 m/s²`，初始速度为零并处于开始滑动状态：

```text
a = g(sin30° - μcos30°) = 2.3562872367 m/s²
s(t) = 0.5 a t²
v(t) = a t
摩擦耗散 W_f = M μ g cos30° s
```

滑块尺寸 2 × 2 × 1 m、质量 10000 kg，8 个节点、12 个三角形；0–5 s、每 0.1 s 一帧。Unity 坡面为 `y = 10 - x tan30°`，下滑方向为 `(+cos30°,-sin30°,0)`，地面横向为 Z。其初始底面贴合此坡面，5 s 后继续向下运动；最终停止播放表示数据结束，不表示物体因摩擦停下。

本算例检验单位、坐标、时间插值、刚体形状、加速度和摩擦功关系。它不含应力、孔压、碎裂、裹挟、冲击或真实三维地形，不是完整山体滑坡模型。来源 CSV 也保存在夹具目录中，可按 manifest 哈希复核。需要实际 LS-DYNA 滑坡时，应以真实模型的结果替换输入，并独立完成材料参数、网格与时间步收敛、能量收支和观测对比。
