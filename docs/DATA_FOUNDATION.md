# Paulatim 数据基础模块 v1（2026-10-12）

用户确认 [长期兼容设计](DATA_COMPATIBILITY_AUDIT.md) 后的第一阶段实现。**已实现隔离开发模块，尚未迁移正式历史或接入计划任务。** 原 `Paulatim.exe`、`PaulatimDispatch.exe`、四列输入及QQ正式发信程序保持原样。

## 已实现

- 独立 `PaulatimData.exe` 与 `PaulatimDataCore.dll`；只有显式本地命令，没有计划任务安装、网络、凭据或邮件调用。
- 单个已登记来源、稳定 source_id、一次性 UUID record_id；旧CSV每个数据记录发生位置独立登记，包括相同内容的两次记录。
- 支持原四列表头/无表头、CSV引号/嵌入换行、非负精确decimal、分钟/秒时间精度。按原BOM探测/UTF-8回退规则读取，不改源字节。
- SHA-256寻址原始归档，以完整临时对象Flush后进行**不覆盖的**原子重命名；同名对象存在时校验字节，不自动修复或覆盖损坏对象。输入解析失败也可留下原始归档，但不能激活成有效数据。
- 独立数据、输入、存储、身份、迁移台账、报告和统计版本，拒绝未知版本或错误字段类型；不将浮点/字符串版本静默强制转换为整数。
- 相同最新快照导入无操作；追加复用旧ID，只分配新后缀的ID。表头、分隔换行或CSV引号表示改变但逻辑字段和顺序相同时也保留ID；实际编辑、插入、重排和缩短拒绝激活。
- 精确内容及业务等值分钟的疑似重复可报告，保留全部记录。内容指纹不作主键，也不自动去重。
- 实际发布 `DS1→DS2` 迁移：只增加 `category=null` 与 `category_missing_reason=not_collected`。不推断IELTS，**不支持手机新列或新的分类输入功能**。迁移ID与该独立源码的散列固定，禁止同ID静默更换实现。
- 按相邻版本解析迁移链；缺失、重复、降级步骤拒绝。当前实际支持DS1、DS2；三步及更多业务Schema需发布新的迁移与验证器后才能激活。
- 完整generation包含记录、身份映射、导入台账、迁移验证结果、兼容视图与manifest；全部验证后只切换 `active.json`。候选、原始对象、ID计划及前代保留。
- 回滚仅允许验证过的祖先，且ID、业务字段、来源与导入边界必须保持一致。新增记录或新导入后拒绝丢弃它们的回滚；没有force开关，也不恢复旧邮件回执。
- `IRecordStore`定义本地存储边界；当前只有文件实现，没有SQLite或NuGet依赖。

## 明确版本与记录契约

| 版本 | 本版定义 |
| --- | --- |
| input_format / input_schema_version | legacy_csv / 1；由适配器声明，原CSV本身仍无版本列 |
| data_schema_version | DS1基础记录；DS2增加未知分类字段 |
| storage_format_version | 1，JSONL与generation清单 |
| identity_policy_version | 1，持久化来源/发生位置登记；不从内容生成ID |
| migration_ledger_version | 1，逐步from/to、源码散列、记录数量、前后语义散列 |
| report_schema_version | 2，兼容旧report-data.json，仍保留原schema_version=2 |
| statistics_version | formal_calendar_v1，正式日历周期；兼容同步视图为rolling_7d_v1 |

DS1记录有 `record_id`、`record_revision=1`、原四个字段的规范表示、`timestamp_input`、`timestamp_precision`、`timestamp_interpretation=legacy_beijing_policy_v1`、来源ID、首次出现的原始快照SHA/数据序号、原四个词法字段数组和 `extensions` 对象。没有补造开始/结束时间、真实时区观测或活动采集时间。DS2只增加上述两个未知分类字段。

分钟在规范数据里保存为精确十进制**文本**，使用decimal验证和计算；兼容旧报告时输出原有JSON数字类型。原始任务空白、数值词法、时间精度与输入出处均能复核。未知业务字段必须走新版Schema，扩展/退役信息可在 `extensions` 中保留；本版不提供历史编辑或字段删除命令。

## 本地私有存储布局

```text
data/
  store.json                          # 持久来源/身份策略登记
  store.lock
  active.json                         # 唯一提交事实
  raw/<sha256>.bin                     # 原始字节，不覆盖
  captures/<capture_id>.json           # 捕获来源、时间、散列；不等于已导入成功
  imports/<sha256>/identity-plan.json  # 准备阶段ID计划，故障恢复复用
  generations/<generation_id>/
    manifest.json                     # 全文件散列、版本、父代、工具身份
    records.jsonl
    identity-map.jsonl
    import-ledger.jsonl
    migration-results.json
    views/report-data.json
    views/work_log.csv                 # 最新完整原始输入字节
```

按整个目录备份和恢复；**只复制records或原CSV不足以保留永久身份。** 不能删除identity-plan、source registry、raw或active以强制重试。`data/`、`paulatim-data/`、构建DLL和生成源码均已加入Git忽略；自选其他存储目录也必须保持私有，不要强制git add运行文件。

候选目录与捕获证据没有自动清理策略。故障恢复以active+manifest为准，不把孤立候选当成成功；本版不提供通用作业journal、保留期限或后台迁移调度器。正常CLI每次命令有成功结果或明确错误；生产健康状态接入属于后续部署工作。

## 仅用合成数据试运行

先从仓库根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tools\Test-Data.ps1
```

测试只在 `_audit/` 建立短路径夹具，不读生产配置/历史、凭据或Gmail，不创建任务。产物中包含三套隔离程序用于实际兼容对照。源码可独立构建：

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Paulatim\Build-Data.ps1 -OutputDirectory .\_audit\data-demo\bin
```

下面示例仅使用虚构记录与新的开发目录；不要替换成生产CSV来“顺便上线”。

```powershell
$demoRoot = Join-Path (Get-Location) '_audit\data-demo'
$demoStore = Join-Path $demoRoot 'data'
$demoSource = Join-Path $demoRoot 'fictional.csv'
$demoExe = Join-Path $demoRoot 'bin\PaulatimData.exe'
[IO.File]::WriteAllText($demoSource, '2030-01-01 09:00,Example task,30,20', (New-Object Text.UTF8Encoding($false)))
$demoSourceId = & $demoExe init --store $demoStore
if ($LASTEXITCODE -ne 0) { throw 'Init failed.' }
& $demoExe import --store $demoStore --source $demoSource --source-id $demoSourceId
if ($LASTEXITCODE -ne 0) { throw 'Import failed.' }
& $demoExe verify --store $demoStore
& $demoExe migrate --store $demoStore --target 2
& $demoExe export-view --store $demoStore --output (Join-Path $demoRoot 'view') --at '2030-01-02T08:00:00.0000000+08:00'
```

`init`仅允许新的或空目录，或验证后复用已有完整store登记；不能在含其他文件的目录猜测/重建来源身份。`export-view`只写新的/空目录，不覆盖历史输出。CLI的时钟使用显式round-trip ISO时间，便于复现日报/周报边界。

回滚需要先从实际已验证manifest确定祖先generation ID：

```powershell
& $demoExe rollback --store $demoStore --generation 'REPLACE_WITH_VERIFIED_ANCESTOR_GENERATION_ID'
```

新增记录后该操作应被拒绝，不能用复制旧active文件绕过检查。

## 验证与恢复行为

读端逐项校验活动指针、manifest清单、每个文件SHA、原始对象、每条记录与原始发生位置的对应、身份map、所有导入计划及边界、迁移代码/前后语义结果，以及重新计算的兼容统计。业务字段不符即使外层文件散列被重新计算也会拒绝。

提交前再次核对正在导入的来源SHA；读取与提交之间有新追加时拒绝旧候选，下一次根据新快照重试。Windows本地文件锁不证明iCloud已收到手机最后一次写入。迁移仅处理已经登记的固定快照，不自动声称它是手机最新数据。

突发退出发生在归档、ID计划、记录文件、全部文件、manifest、指针切换之前时，旧代仍有效；发生在指针成功切换后时，新代有效，即使CLI没来得及报告成功。再次执行先读取有效指针，并复用准备过的ID，不能盲目恢复旧目录。

本轮测试结果与可复现范围见 [data-foundation-results.json](data-foundation-results.json)。覆盖旧输入/永久ID/增量/重复/未知版本/来源歧义/只增null迁移/回滚/损坏检查/备份恢复、真实子进程中断、CLI命令及原报告对照。

最终数据基础检查 **54/54通过**，包含7个实际子进程退出点、活动指针文件锁导致的真实替换失败、迁移/回滚异常、语义与文件损坏、完整目录备份恢复，以及原同步统计/正式周报HTML与原字节附件对照。原程序的22项同步、28项dispatcher离线检查及4项集成检查亦通过，共 **54/54**；没有真实发信。

## 生产启用前剩余工作

- 确认真实Shortcuts动作、CSV来源是否曾被编辑/重排，以及准备首次身份登记的具体差异与完整备份。没有ID的同内容旧记录依旧无法证明各自实体活动身份；不会自动猜测。
- 确认并实现所有生产入口的兼容切换，包括当前旧同步任务；不得只换邮件程序却让旧同步器继续覆盖新兼容状态。正式dispatcher目前没有接入本模块，export视图不会自动替换生产文件。
- 建立独立备份位置/ACL及恢复检查。应用层禁止覆盖原始对象，不等于防管理员篡改的WORM；SHA校验也不等于签名认证。
- 本版单来源、四列，DS2只表达历史缺失分类；多来源、新手机ID契约、人工身份对账/修订、更多Schema、SQLite均需后续实现和验收。
- 16 MiB来源限制、整代快照空间和全历史邮件大小限制仍存在；满盘、真实断电、实机iCloud传播和所有Windows/文件系统组合没有全面验证。已测试的是进程终止与受控I/O失败，不能将其描述为真实断电测试。
- 当前.NET Framework路径长度有限；使用短开发路径，路径/I/O失败会阻止激活。错误不会截断记录，但需要清楚报告而不是不断后台重试。

上述生产工作需具体方案与授权后再执行。本轮仅批准开发和GitHub交付，不迁移真实记录，不改变正式发信或手机格式。
