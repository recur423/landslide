# 降雨滑坡场地准备与 LS-DYNA 接口

该流程把**实测或有来源的场地数据**整理为三维网格和时间降雨边界，并审计外部水文、滑面计算结果。它不推断未知的地层和材料参数，不求解非饱和入渗、边坡稳定或 LS-DYNA 动力学。工程目前没有本次滑坡的这些现场资料和真实 LS-DYNA 结果，因此运行完本工具也只能得到 `prepared-not-solved` 报告。解析测试数据仍须标为 `ANALYTIC VERIFICATION`。

## 1. 准备场地资料

从滑前 DEM/地面点云生成地表高程，在相同**投影坐标系和垂直基准**下，依据钻孔、露头、探槽及地球物理剖面建立土层界面和基岩面。用同一矩形投影网格输出每张曲面，保留勘测日期、采样精度、插值方法和不确定区域。坡脚、沟谷、坡顶及突变线要在栅格化前作为 breaklines 处理；不能只靠坡度阈值推断。若原始数据是经纬度，先投影到具有明确长度单位的平面坐标。点云和 DEM 的分类、配准、插值由测绘/GIS 流程完成，本工具不伪装为点云处理器。

复制 [`case_template.json`](../Tools/LSDyna/case_template.json)，把空值替换为真实来源。`surfaceGridCsv` 是滑前地表；`layers` 从上到下指定每层**底面**及唯一正 `partId`。最下层底面通常为建模域底边，不应把未经勘察的基岩面当作已知。界面必须覆盖同一完整网格，且每个点严格低于上层。局部原点使用源坐标及源长度单位；输出的节点坐标在减去原点后转为米，避免大地坐标造成精度损失。

地表和各层底面的 CSV 都严格使用：

```csv
i,j,x,y,z
```

`i,j` 为从 0 起连续的网格索引；`x` 随 `i` 递增、`y` 随 `j` 递增。工具生成共享层间节点的八节点实体单元、`surface_cells.csv` 中的坡度、下坡方向、投影面积及降雨区编号。`mesh_only.k` 只是可纳入完整关键字模型的 `*NODE`/`*ELEMENT_SOLID` 几何片段；**没有** `*PART`、`*SECTION_SOLID`、材料、重力、接触或边界条件，不能独立求解。网格质量、剪切带分辨率、计算域边界和弱夹层的物理定义仍须由建模人员核查。

可选的 `terrainFeaturesCsv` 明确记录现场辨认的坡顶、坡脚、沟谷、滑缘等线：

```csv
feature_id,kind,sequence,i,j
```

`kind` 可为 `crown`、`toe`、`gully`、`breakline` 或 `scarp`；同一特征的 `sequence` 从 0 连续编号，点必须沿现有网格边相邻。输出 `terrain_feature_vertices.csv` 保留对应网格节点 ID。该文件只是几何约束的可追溯标注；需在网格生成前使实测折线与网格边一致。缺少文件时报告 `terrainFeaturesMissing=true`，不会凭坡度编造坡脚和沟谷。

## 2. 输入降雨与前期状态

`rainfallCsv` 使用以下严格列名和 SI 单位：

```csv
zone_id,start_s,end_s,intensity_m_per_s
```

每行表示左闭右开的恒定雨强时段。同一区域时段不得有间断或重叠，所有区域需覆盖共同的时间范围与 `eventStartSeconds`。事件前时段代表前期降雨；`case_report.json` 分别统计事件前与全历程的**雨深**。这些雨深是大气输入，不是入渗深度。若降雨有空间分区，另用 `cellZonesCsv` 为每个地表单元指派区域：

```csv
i,j,zone_id
```

雨量由水平投影面积积分，`rainfall_intervals.csv` 和 `cell_rainfall_zones.csv` 可交给外部水文模型。预热阶段须利用事前降雨、蒸散和实测地下水位/含水率建立初始状态；缺少时要报告为未知，不可任设“全干”或“全饱和”。如果已有事发前实测初始状态，应避免再重复施加同一段前期降雨。

## 3. 渗流与水量审计

使用经过土水特征曲线、相对导水率和渗透试验标定的非饱和渗流模型，结合坡面蓄水/径流及地下水边界，计算含水率、饱和度、吸力、孔压和出流随时间的变化。需要时进行三维分析，尤其是基岩界面、沟谷汇水与坡脚出渗。降雨强度不应直接当作全部入渗通量。

可选 `hydroBudgetCsv` 对外部模型的分时段水量进行审计：

```csv
start_s,end_s,precipitation_m3,groundwater_in_m3,runoff_out_m3,groundwater_out_m3,evapotranspiration_m3,storage_change_m3,infiltration_m3
```

报告同时给出由雨强积分的降雨体积、与模型上报降雨量的差值，以及整个域的水量残差：

```text
降雨 + 地下水流入 - 径流流出 - 地下水流出 - 蒸散 - 储水变化
```

`infiltration_m3` 是内部通量诊断，不在整个域的出入水账本中再扣一次。工具报告残差，**不擅自设定合格阈值**；必须按观测误差和模型精度解释。仅靠水量守恒还不能证明孔压时程正确，应另与水位计、张力计和含水率传感器比对。

## 4. 材料、初始平衡与稳定性

以下参数需要现场或室内试验与有记录的校准，不能从 DEM、现有 RAMMS 帧或 Unity 动画确定：密度与孔隙率、水平/竖向渗透系数、土水特征曲线、有效强度、刚度、剪胀、峰后软化、残余强度、基岩节理和滑面接触参数。场地资料能确定的仅是测点处的几何、雨量、水位、含水率或位移；测点间仍有插值不确定性。证据文件路径可填在 `evidence` 的对应项，报告记录哈希与缺项。

真实 LS-DYNA 模型需另行定义材料、边界及重力，先使自重、初始地下水和应力平衡，核对支承反力、残余力、动能和初始塑性变形。降雨渗流的孔压/含水率场映射到动力模型时，需要对同一坐标、网格、单位和时刻做点对点验证；一个场地准备报告并不表示这种映射已完成。

可选 `slipPatchesCsv` 对**已给定滑面、饱和且法向受压**的外部计算结果做有效应力强度筛查：

```csv
time_s,patch_id,total_normal_stress_pa,shear_stress_pa,pore_pressure_pa,cohesion_pa,friction_angle_deg,area_m2
```

对每个时刻求 `FS = Σ[c′ + (σn-u)tanφ′]A / ΣτA`，报告首次从大于 1 到不大于 1 的**输出时刻区间**。拒绝负孔压或孔压大于总法向应力的行，以免把非饱和吸力或张开界面偷偷套进这一饱和受压公式。它不是三维失稳证明；还要考察塑性区贯通、位移加速、候选滑面竞争和监测记录。

## 5. LS-DYNA 适用范围与动力结果

公开的 [LS-DYNA 关键词手册](https://lsdyna.ansys.com/manuals-download/)列有孔流与固结控制；R16 的 `*CONTROL_PORE_FLUID` 包括排水、不排水及随时间变化的固结选项，`*BOUNDARY_PWP` 可输入水头或节点体积流量。节点流量不能直接填写以长度/时间表示的雨强，且这些卡本身不自动处理完整的非饱和持水关系、入渗能力、坡面蓄水和径流。`SUCLIM` 是吸力限制，并非 Richards 入渗求解。实际可用功能还取决于**确切的求解器版本、许可、材料和单元组合**；须用小算例核实，不能据手册标题推断整个过程已耦合。参见 [R16 关键词手册](https://lsdyna.ansys.com/wp-content/uploads/2025/04/LS-DYNA_Manual_Vol_I_R16.pdf)及 [R17 发布说明](https://lsdyna.ansys.com/wp-content/uploads/2026/10/Release_Notes_LS-DYNA_R17_0_0_rev0.html)。

失稳后的 LS-DYNA 计算需选择能够表示实际剪切、接触、碎裂和堆积的材料及大变形方案，比较起滑时刻、速度、滑程、堆积范围、质量和能量账本，并做网格、时间步、质量缩放及参数敏感性分析。若求解中发生侵蚀或拓扑变化，当前 Unity 固定拓扑回放格式不能忠实表示，应扩展帧级拓扑后再导入。

## 6. 运行与结果接入

在 `Unity_Project` 目录运行，将路径换成**已填写且对应真实场地数据**的案例文件：

```powershell
python Tools/LSDyna/prepare_rainfall_case.py --case D:/Survey/case.json --output D:/Survey/prepared-case
```

输出包括 `mesh_only.k`、`surface_cells.csv`、雨强及分区 CSV、可选地貌折线、`case_report.json`。目标目录必须不存在；输入校验失败不会发布半套成果。`case_report.json` 总是 `prepared-not-solved`，列出缺少的现场或求解证据。模板中的空值会被拒绝，目的是防止把未知参数当成默认值。

取得真正的 LS-DYNA 求解结果并按[结果接入格式](LSDynaDataFormat.md)导出后，可在转换时加 `--site-case-report D:/Survey/prepared-case/case_report.json`。转换器把报告及 SHA-256 引用复制进数据包；Unity 检查其完整性，显示案例 ID 与缺项数，**仍不声称物理验证通过**。只有对照独立观测和求解器诊断，才能判断该模型是否适用于这次滑坡。

测试命令：

```powershell
python -B -m unittest discover -s Tools/LSDyna/tests -v
```

测试用的是明确标注的人工微型网格和数字，仅核查网格拓扑、降雨体积、水量残差、饱和滑面筛查及错误输入拒绝；不构成 Hunnedalen 场地资料。
