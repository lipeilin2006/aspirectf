FROM debian:trixie-slim
COPY run.sh /opt/run.sh
RUN chmod +x /opt/run.sh
WORKDIR /workspace
ENTRYPOINT ["/opt/run.sh"]
