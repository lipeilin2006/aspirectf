"""Demo Web 题：登录框 SQL 注入（教学用故意脆弱代码）。

靶场启动后监听 :8088，admin 账号登录成功会返回 flag。
"""

import os
import sqlite3

from flask import Flask, jsonify, request

DB_PATH = "/tmp/users.db"
FLAG = os.environ.get("DEMO_FLAG", "FLAG{demo_sqli_on_login_form}")

app = Flask(__name__)


def init_db() -> None:
    conn = sqlite3.connect(DB_PATH)
    conn.execute("CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY, username TEXT, password TEXT, role TEXT)")
    conn.execute("DELETE FROM users")
    conn.execute("INSERT INTO users (username, password, role) VALUES ('guest', 'guest123', 'user')")
    conn.execute("INSERT INTO users (username, password, role) VALUES ('admin', 'S3cretAdm1n!', 'admin')")
    conn.commit()
    conn.close()


@app.get("/login")
def login_page():
    return (
        "<h1>Demo Login</h1>"
        "<form method='post'>"
        "<input name='username' placeholder='user'><br>"
        "<input name='password' type='password' placeholder='pass'><br>"
        "<button type='submit'>login</button>"
        "</form>"
    )


@app.post("/login")
def login():
    username = request.form.get("username", "")
    password = request.form.get("password", "")

    # NOTE: 故意存在 SQL 注入（CTF 教学题目）
    query = f"SELECT id, username, password, role FROM users WHERE username='{username}' AND password='{password}'"

    conn = sqlite3.connect(DB_PATH)
    try:
        row = conn.execute(query).fetchone()
    except Exception as exc:  # noqa: BLE001 - 故意回显 SQL 错误以放大注入面
        return jsonify({"error": str(exc), "query": query}), 400
    finally:
        conn.close()

    if row is None:
        return jsonify({"status": "denied"}), 401

    if row[3] == "admin":
        return jsonify({"status": "ok", "role": "admin", "flag": FLAG})

    return jsonify({"status": "ok", "role": row[3]})


if __name__ == "__main__":
    init_db()
    app.run(host="0.0.0.0", port=8088, debug=False)
