#!/bin/sh
set -eu
mkdir -p /fixture
if [ ! -f /fixture/client ]; then
  ssh-keygen -q -t ed25519 -N '' -f /fixture/client
fi
ssh-keygen -A
cp /fixture/client.pub /home/deploy/.ssh/authorized_keys
chmod 700 /home/deploy/.ssh
chmod 600 /home/deploy/.ssh/authorized_keys
chown -R deploy:deploy /home/deploy/.ssh /opt/apps
cat > /etc/ssh/sshd_config <<'EOF'
Port 22
HostKey /etc/ssh/ssh_host_ed25519_key
PermitRootLogin no
PasswordAuthentication no
PubkeyAuthentication yes
UsePAM no
AuthorizedKeysFile .ssh/authorized_keys
Subsystem sftp internal-sftp
AllowUsers deploy
LogLevel ERROR
EOF
exec /usr/sbin/sshd -D -e
