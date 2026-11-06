#!/bin/sh
set -eu
mkdir -p /fixture
# Existing application fixture: discovery must import it without executing its commands.
recovery_root=/opt/apps/recover-fixture
recovery_release=$recovery_root/releases/22222222222222222222222222222222
mkdir -p "$recovery_release"
if [ ! -d "$recovery_release/.git" ]; then
  git -C "$recovery_release" init -q --initial-branch=main
  git -C "$recovery_release" remote add origin https://github.com/fixture/recovery-only.git
fi
printf '%s\n' 11111111111111111111111111111111 > "$recovery_root/.deployit-project"
printf '%s\n' 'RECOVERY_KEEP=original' > "$recovery_root/.env"
ln -sfn "$recovery_release" "$recovery_root/current"
cat > "$recovery_root/.deployit-project.json" <<'EOF'
{"Version":1,"Id":"11111111-1111-1111-1111-111111111111","Name":"Recovery fixture","RepositoryId":"","RepositoryName":"fixture/recovery-only","RepositoryUrl":"https://github.com/fixture/recovery-only.git","Branch":"main","DeployCommand":"touch /opt/apps/recover-fixture/MUST_NOT_RUN","RestartCommand":"touch /opt/apps/recover-fixture/MUST_NOT_RUN","StopCommand":"touch /opt/apps/recover-fixture/MUST_NOT_RUN","TimeoutMinutes":20}
EOF
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
