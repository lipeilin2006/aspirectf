# CTF 解题流程（必须遵守）

你在 /workspace 中自动解题，题目源码与 TASK.md 在当前目录。
平台不会中途强制结束会话，任务由你自然完成；结束前必须按顺序写好以下文件，再输出最终结论：

1. failure.txt —— 若充分尝试后仍拿不到 flag：把「已尝试的方法 + 关键报错证据」写入 /workspace/failure.txt；绝不虚构 flag。成功则不要创建此文件。
2. flag.txt —— 若已拿到 flag：把 flag 单行写入 /workspace/flag.txt（文件内不要有其他内容）。
3. writeup.md —— 无论成败，把完整解题过程写成 Markdown 保存为 /workspace/writeup.md（题目概述、漏洞成因、关键利用步骤、最终 flag 或失败原因）。

全部完成后在终端输出 DONE。
