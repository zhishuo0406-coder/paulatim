# WorkLogSync 接管仓库

完整项目已从本机既有交付ZIP接收，保留原源代码与开发文档。本轮仅进行接收、独立审查、隔离测试和Git管理准备，没有部署、发信或改动现有任务。

- 当前程序与使用说明：[WorkLogSync/README.md](WorkLogSync/README.md)
- 原交接：[HANDOFF.md](WorkLogSync/HANDOFF.md)
- 原开发路线：[DEVELOPMENT_ROADMAP.md](WorkLogSync/DEVELOPMENT_ROADMAP.md)
- 本轮审查：[REVIEW-2026-10-11.md](docs/REVIEW-2026-10-11.md)
- 优先级、收益、工时和验收：[PRIORITIES.md](docs/PRIORITIES.md)
- GitHub迁移与公开发布检查：[GITHUB_MIGRATION.md](docs/GITHUB_MIGRATION.md)
- 决策记录：[DECISIONS.md](docs/DECISIONS.md)

项目目录为 `WorkLogSync/`。源代码依赖Windows .NET Framework系统编译器和库，不使用NuGet或付费模型API。邮件依赖现有Codex的ChatGPT登录及Gmail连接，不包含账户凭据。

本轮54项原离线检查通过；新增11项独立检查中8项通过，3项暴露待处理问题，详见审查报告。不要把历史测试报告当作所有功能已经可靠的证明。当前不建议用接管仓库直接替换现有安装。

独立检查可在仓库根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tools\Review-Offline.ps1
```

它只在 `_audit/` 中使用合成数据，不发信、不注册任务、不读取真实工时文件。当前有3项未满足的安全/可靠性期望，退出码为1是预期的审查结果；应修复相应问题后再要求该检查全绿。

工作目录下exe及本地测试输出不提交Git。`WorkLogSync/MANIFEST.sha256` 是原ZIP的归档清单，包含归档中的exe；它不是Git克隆完整性的校验依据，Git克隆后应从源码构建。UTF-8文本和明确的换行设置为未来macOS开发保留空间，目前程序仍只支持Windows。

用户已在2026-10-11确认上传，并将目标可见性从Private改为Public。目标为 `zhishuo0406-coder/worklog-sync`。首次公开推送须复核39个文件、全部提交历史和作者元数据，排除个人数据及凭据；提交作者使用该账号的GitHub隐私邮箱。连接和上传是否成功以远端可见性、提交哈希及文件清单核验为准。上传不代表部署或修复已知问题。
