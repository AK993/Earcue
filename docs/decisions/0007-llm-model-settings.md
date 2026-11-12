# ADR-007: 模型 API Key 管理与模型发现

## 背景

M4 的回答层依赖云端 LLM。最初的 `LlmOptions` 只有 `baseUrl` / `model` / `apiKey`，Key 会以明文写入 `config.json`；用户也无法在软件内填写 Key、选择模型，或确认"这个 Key 到底能调用哪些模型"。会议现场更换模型（例如从 DeepSeek 切到本地 Ollama）需要手改配置文件，不符合"设置页可完成一切"的产品要求。

## 决策

1. **设置页统一管理**：服务商预设 → Base URL → API Key → 模型 ID / 模型列表 → 测试 → 保存。保存后立即重建 `OpenAiCompatibleLlmClient`，无需重启。
2. **模型发现走 OpenAI 兼容接口**：`GET {baseUrl}/models`，请求头 `Authorization: Bearer {key}`。裸域名先尝试 `/v1/models`，再退回 `/models`；带路径的 Base URL 只尝试 `{base}/models`。
3. **模型列表只是建议，不是白名单**：模型 ID 使用可编辑输入框，列表点击即填入。原因：部分服务商（如火山方舟）的 `model` 字段是接入点 ID（`ep-...`），无法从 `/models` 推断；部分代理也不实现该接口。
4. **保存前可验证**：`测试调用` 发送一次极小的非流式 `chat/completions`（`max_tokens=16`），返回往返耗时与回复摘要，避免"列表里有模型但实际不可用"。
5. **Key 只以 DPAPI 密文落盘**：使用 `CryptProtectData`（`CRYPTPROTECT_UI_FORBIDDEN`，CurrentUser 范围）+ 固定附加熵，写入 `llm.apiKeyProtected`（`dpapi:v1:` + Base64）。`llm.apiKey` 标记 `[JsonIgnore]`，只存在于进程内存，永不写入配置文件或日志。
6. **本地模型允许空 Key**：`localhost` / `127.0.0.1` / `::1` 不强制填写 Key；云端地址未填 Key 时保存会二次确认，并在提问时引导打开设置页。
7. **超时与降级**：模型列表与测试调用各 25 秒超时；网络/401/403/404/429/5xx 都转换为中文可操作提示，不弹异常栈、不阻塞 UI。

## 备选方案

- **把 Key 明文放 config.json**：实现最简单，但一旦用户分享配置、上传日志或打包诊断信息就会泄露；否决。
- **Windows 凭据管理器（Credential Manager）**：跨机器同步体验更好，但 API 调用复杂、需要 P/Invoke `advapi32`，且当前仅需单机单用户；DPAPI 足够且文件随配置一起迁移时自然失效。
- **把模型列表写死**：模型迭代快，写死很快过期；动态发现 + 可编辑输入框更稳。
- **在设置页保存模型列表缓存**：列表随 Key 权限与时间变化，缓存会误导；每次打开按需拉取。

## 后果

- `config.json` 复制到其他 Windows 用户或电脑后，Key 无法解密（DPAPI 绑定用户 + 机器），界面会提示"已加密保存但无法解密，请重新填写"。这是预期安全行为。
- `/models` 不是 OpenAI 规范强制接口；失败时用户仍可手动输入模型，不影响主流程。
- 服务商预设中的模型名只是 UI 建议值，真实可用性以各服务商控制台与 `/models` 为准。
- Key 明文只存在于内存；如果进程被调试或内存转储，仍可能被读取，这超出本 ADR 的威胁模型。

## 涉及代码

- `src/Earcue.Core/Security/ApiKeyProtector.cs`：DPAPI 加解密。
- `src/Earcue.Core/Configuration/EarcueConfig.cs`：`LlmOptions.SetApiKey/TryLoadApiKey`、加密字段。
- `src/Earcue.Core/Configuration/LlmProviderPresets.cs`：服务商预设与显示名解析。
- `src/Earcue.Brain/Llm/LlmEndpoints.cs`：Base URL 规范化与接口候选地址。
- `src/Earcue.Brain/Llm/OpenAiCompatibleModelCatalog.cs`：模型列表、连通性测试、错误映射。
- `src/Earcue.App/SettingsWindow.xaml{,.cs}`：设置界面。
- `src/Earcue.App/MainWindow.xaml{,.cs}`：状态展示、保存后热切换、未配置引导。
