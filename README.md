# FAKE ESP - 透视模拟器

这是一个为主播或者做视频设计的纯视觉效果3D假透视，适配OBS，也可以通过覆盖层显示在最上方，完全独立于游戏进程运行。

## 📋 运行要求

- **Windows 10 / 11（64 位）**
- **需要 .NET 9 桌面运行时**。没装的话双击 exe 会弹一个提示并给出下载链接，
  装好再运行即可。下载地址：<https://dotnet.microsoft.com/download/dotnet/9.0>
  （选 **Desktop Runtime**，不是 SDK）
- **第一次运行会弹 Windows 的安全提示**（因为这个 exe 没有购买数字签名证书）。
  点 **「更多信息」→「仍要运行」** 即可，一般点过一次就不再提示。

## ⚠️ 免责声明

- **免责声明**：仅供娱乐，直播整活，未提供真实的作弊能力，如本项目违反社区规则请通知我。
- **风险声明**：本项目理论上不读写任何游戏内存，不注入DLL，不挂钩API，但本人未测试所有反作弊系统下的效果，不能保证在反作弊系统下绝对安全，使用者在任何游戏中使用本程序产生的封号风险，应由使用者自行承担。
- **声明性质**：这是一个“视觉障眼法”，不是真正的外挂。

## 🚀 使用教程

1. 打开obs，添加浏览器源
2. 打开exe程序，复制提供的地址，一般是 `http://127.0.0.1:17777/?mode=obs`
3. 把地址复制到obs浏览器源的链接框里，自行设置其他obs源参数

> **可选**：`obs/FAKE-ESP.json` 是一份现成的 OBS 场景集合（底层画面 + 覆盖层已经合好了），
> 在 OBS 菜单 **「场景集合 → 导入」** 里选它就能直接用。想自己手动加浏览器源也完全没问题，
> 两种方式效果一样。

## ⌨️ 热键

- `Ins`键：隐藏/显示渲染效果
- `Home`键：重置视角

## 📄 许可

[Unlicense](LICENSE) —— 已释放到公有领域。**随便用、随便改、随便发、商用也行，不需要署名。**

---

# FAKE ESP - Perspective Simulator

A purely visual 3D fake ESP designed for streamers or video creators. Compatible with OBS, and can also be displayed as an overlay on top. Runs completely independently of the game process.

## 📋 Requirements

- **Windows 10 / 11 (64-bit)**
- **.NET 9 Desktop Runtime is required.** If it is missing, double-clicking the exe shows a prompt
  with a download link — install it and run again: <https://dotnet.microsoft.com/download/dotnet/9.0>
  (pick **Desktop Runtime**, not the SDK)
- **The first launch shows a Windows security warning** (this exe is not code-signed).
  Just click **"More info" → "Run anyway"**. It normally only appears once.

## ⚠️ Disclaimer

- **Disclaimer**: For entertainment and streaming purposes only. It does not provide real cheating capabilities. If this project violates any community rules, please notify me.
- **Risk Statement**: Theoretically, this project does not read/write any game memory, inject DLLs, or hook APIs. However, I have not tested it against all anti-cheat systems and cannot guarantee absolute safety. The risk of account bans from using this program in any game shall be borne solely by the user.
- **Nature**: This is a "visual illusion," not a real cheat.

## 🚀 Tutorial

1. Open OBS, add a Browser Source.
2. Open the exe program, copy the provided address, usually `http://127.0.0.1:17777/?mode=obs`.
3. Paste the address into the URL field of the OBS Browser Source, and configure other OBS source parameters as needed.

> **Optional**: `obs/FAKE-ESP.json` is a ready-made OBS scene collection (game capture + overlay
> already composited). Import it via **Scene Collection → Import**. Adding a Browser Source
> manually works just as well.

## ⌨️ Hotkeys

- `Ins` key: Hide/Show rendering effects.
- `Home` key: Reset view.

## 📄 License

[Unlicense](LICENSE) — released into the public domain. **Use it, modify it, ship it, sell it;
no attribution required.**
