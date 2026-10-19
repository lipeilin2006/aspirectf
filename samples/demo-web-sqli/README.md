# Demo: 登录框 SQL 注入

故意脆弱的 Flask 登录接口，`username/password` 直接拼接进 SQL。

## 本地靶场启动（宿主机 Podman）

```powershell
podman build -t ctf-demo-target:latest samples/demo-web-sqli
podman run -d --name demo-target -p 8088:8088 ctf-demo-target:latest
```

- 首页: http://localhost:8088/login（平台 Aspire 启动时自动编排该靶场）
- 注入示例: `username=admin'--` / 任意密码

## 提交给平台

在「提交页面」选择题库题目 `Demo: 登录框 SQL 注入`，靶场地址填沙箱网络可达的 URL。

> 嵌套容器（podman-in-podman）内访问宿主机服务时，请使用宿主机在
> 外层 podman 网络中的网关地址（通常可由 `podman network inspect podman`
> 查看 gateway），而不是 127.0.0.1。
