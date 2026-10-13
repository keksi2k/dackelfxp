FROM ubuntu:24.04
RUN apt-get update && apt-get install -y --no-install-recommends vsftpd openssl && rm -rf /var/lib/apt/lists/* \
 && useradd -m -s /bin/bash dackel && echo 'dackel:dackel' | chpasswd \
 && mkdir -p /home/dackel/ftp /var/run/vsftpd/empty && chown dackel:dackel /home/dackel/ftp \
 && openssl req -x509 -nodes -newkey rsa:2048 -days 365 -subj "/CN=vsftpd" \
      -keyout /etc/ssl/private/vsftpd.key -out /etc/ssl/certs/vsftpd.pem
COPY vsftpd.conf /etc/vsftpd.conf
EXPOSE 21 30000-30019
CMD ["/usr/sbin/vsftpd", "/etc/vsftpd.conf"]
