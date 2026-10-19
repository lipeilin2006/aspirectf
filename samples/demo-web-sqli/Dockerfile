FROM docker.1ms.run/library/python:3.13-slim
WORKDIR /app
COPY app.py .
RUN pip install --no-cache-dir flask
ENV DEMO_FLAG=FLAG{demo_sqli_on_login_form}
EXPOSE 8088
CMD ["python", "app.py"]
