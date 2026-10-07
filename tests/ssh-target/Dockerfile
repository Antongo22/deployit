FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    openssh-server git bash ca-certificates coreutils util-linux \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -m -s /bin/bash deploy && passwd -d deploy \
    && mkdir -p /run/sshd /home/deploy/.ssh /opt/apps \
    && chown -R deploy:deploy /home/deploy /opt/apps
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 22
ENTRYPOINT ["/entrypoint.sh"]
