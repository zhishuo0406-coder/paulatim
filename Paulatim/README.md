# Paulatim — Windows工时同步与自动邮件

2026-10-12更新：开发目录、源文件、构建输出和新任务名已统一为Paulatim；新发信主题也使用Paulatim，兼容旧WorkLog主题与回执。CSV及状态文件名保留。新增独立QQ发件与Windows凭据库支持，实际验证及部署状态见 `../docs/MAIL_SENDER_MIGRATION.md`。实际运行安装不会自动迁移。原始归档文件名与哈希保存在 `../docs/archive/ORIGINAL_DELIVERY_MANIFEST.sha256`。安装器在旧任务存在时拒绝建立第二套入口，迁移须单独审阅和授权。

iPhone Shortcuts把工时CSV写入iCloud；Windows把已同步的文件校验并镜像到本地，生成统计，支持独立QQ SMTP发送日报、周报及晚开机补发。原Codex/Gmail路径作为旧选项保留。

仓库包含源码、构建/配置/安装/检查脚本、配置模板和脱敏测试报告；Git克隆不含exe，需本机构建。没有个人工时记录、真实邮箱、账号凭据、Token、运行日志或发送回执。示例CSV全部为虚构数据。旧PowerShell/VBS定时入口已经淘汰。

## 已实现

- CSV四列：北京时间日期时间、事项、实际分钟、有效分钟。支持无表头、表头、UTF-8、引号、逗号和换行，第一条记录不会丢失。
- 无窗口程序每分钟同步；排除并发写入，校验字段，原子替换，保留上一份有效副本；非法或缺失来源不覆盖有效数据。
- 每天北京时间08:00发昨日日报，周日另发前七个完整自然日周报。
- 错过08:00，在Windows开机登录后检查并补发；网络、代理、iCloud未就绪时自动等待后续检查。
- 并发锁、失败退避、尝试上限、执行超时。QQ持久化发送意图及冻结快照，SMTP接受后记账，未知结果不盲目重试。旧Gmail路径要求执行器查询及读回，但控制器尚未独立核验其回执。
- 完整CSV原始快照同时出现在正文和附件；重复或异常分钟记录保留并提示。
- 连续多天离线，只补最近一期到期日报和周报；CSV保留完整历史。

## Windows环境

需要64位Windows、Windows PowerShell 5.1或PowerShell 7、.NET Framework 4.x及其系统编译器。QQ发信需要开启SMTP、客户端授权码保存在任务用户的Windows凭据库，以及能访问smtp.qq.com:587；安全配置脚本使用Windows PowerShell 5.1。旧Gmail路径另需Codex、ChatGPT登录、Gmail连接、网络/代理和使用额度。无需付费模型API；QQ发送不调用模型。

当前任务安装脚本要求Windows时区为China Standard Time。同步及统计固定使用北京时间。开机补发实际指用户登录后开始处理，不能在关机期间投递，也不能保证登录的瞬间收到邮件。

## 在Windows Codex中打开与验证

1. 解压ZIP到一个新目录，例如 `D:\Paulatim-Delivery`。打开其中的 `Paulatim` 文件夹。不要覆盖当前正在运行的安装目录。
2. 在Windows Codex中将这个文件夹添加/打开为项目，新建聊天，要求先阅读 `HANDOFF.md`、`README.md` 和 `DEVELOPMENT_ROADMAP.md`。
3. 在该目录的PowerShell终端执行下面的构建及离线测试。全部使用合成测试数据，不发送邮件，不注册计划任务。

```powershell
.\Build.ps1
.\Build-Mail.ps1
.\Test-Paulatim.ps1
.\Test-Mail.ps1 -Offline
```

若本机已经有有效ChatGPT登录，可另外运行 `.\Test-Mail.ps1` 检查登录前置条件；它不调用模型或发送邮件。完整测试结果与本次交付验证见 `TEST_REPORT.md`。

Windows PowerShell 5.1的默认脚本策略可能提示“禁止运行脚本”。不用修改全局策略，可让本地Codex为单次可信本地脚本进程指定RemoteSigned。例如：

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Build.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Build-Mail.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Test-Paulatim.ps1
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Test-Mail.ps1 -Offline
```

若ZIP带有下载来源标记，先在ZIP文件属性中解除阻止，再解压。若设备有组织强制策略，不要绕过，应由本地Codex说明限制。配置和安装脚本也可按相同的单次进程方式调用。

## 初次配置及运行

请让本地Codex按照下面的顺序完成。`Configure.ps1` 不覆盖已有设置，不启动任务，邮件默认关闭。

```powershell
# 用本人邮箱和实际iCloud CSV路径替换示例参数
.\Configure.ps1 -SourceCsv 'C:\实际路径\work_log.csv' -Recipient '本人邮箱@gmail.com'
.\Build.ps1
.\Build-Mail.ps1
.\Paulatim.exe
```

检查 `sync-status.json` 的 `success` 和 `report-data.json` 中的数据完整性。邮箱现在是配置项，保存在本地 `mail-config.json`，不需要编辑源代码。`first_daily_report` 是允许发送的最早记录日期；`first_weekly_end` 是最早周报的周六日期，配置脚本按首次日期计算。

选用QQ时，保持普通邮件disabled，用Windows PowerShell 5.1运行 `Configure-Smtp.ps1 -Sender 'sender@qq.com' -Recipient 'recipient@gmail.com'`（替换虚构地址）。仅在本机安全输入框输入QQ客户端授权码，不在命令行或聊天输入。配置脚本保存凭据库引用，选择qq_smtp，不开启正式邮件。旧Gmail选项则需要连接Gmail并确认Codex采用ChatGPT账户登录。

运行 `.\PaulatimDispatch.exe --verification` 会向配置邮箱发送真实邮件，含当前来源CSV；必须已有本人授权。随后在收件箱验证发件身份、正文及原始附件字节；`smtp_accepted`仅表示服务器接受，不表示实收。测试完成且部署获准后将 `mail-config.json` 的 `enabled` 改为 `true`，再安装任务：

```powershell
.\Install-LocalSync.ps1
.\Install-Mail.ps1
```

注册任务可能需要Windows允许本次操作。已有同名任务时安装脚本拒绝随意覆盖；尤其不要在新解压目录里覆盖现有D盘任务。迁移/升级先备份本地配置、CSV和回执，暂停旧发送入口，再由本地Codex审阅任务路径后执行。不要同时保留两个邮件发送入口。

## 状态与暂停

同步状态：`sync-status.json`。邮件状态：`mail-status.json`。`idle` 为没有到期报告，`waiting_network` / `waiting_source` 为自动等待，`waiting_retry` 为退避等待，`attention_required` / `failed` 为需要检查。QQ记录在 `smtp-state.json`；旧路径证据在本机 `outbox`。不要删除任一发送状态来强行重试。未知发送结果必须检查收件箱后审阅恢复。

暂停邮件：禁用Windows任务 `PaulatimMail-Dispatch`，或把本地邮件配置的 `enabled` 改为 `false`。暂停同步：禁用 `Paulatim-iCloud-to-D`。正常同步和发送都不需要保持桌面窗口打开；QQ路径依赖任务用户的本机凭据库，旧路径依赖Codex登录。

## 在其他聊天读取

在支持且已授权Gmail连接的ChatGPT聊天里请求：“查找最新Paulatim工时邮件，兼查历史WorkLog邮件，读取正文完整原始快照，第一行也是记录，说明快照时间后帮我复盘。”普通云端聊天不能直接读取Windows本地文件。不是所有GPT都有Gmail工具；邮箱数据截至最近一次报告快照。

## 文件导航

`Paulatim.cs`：数据同步、CSV校验和统计。`PaulatimDispatch.cs`：到期判断、报告组装及邮件控制。`Build*.ps1`：重建无控制台程序。`Install*.ps1`：安装本地任务。`Test*.ps1`：隔离测试。`*.example.json`：配置模板。`mail-result-schema.json`：邮件执行器结果契约。`verification/`：脱敏测试结果。`HANDOFF.md`：接手开发约束。`DEVELOPMENT_ROADMAP.md`：尚未完成的增强项。
