# Preview runner: takes one generated React + Python package (a zip served by the API), builds it and serves the
# whole app (back end API and built front end) on one port, so it can be opened in a browser pane or an iframe.
# It runs model-written code, so it is deployed ONLY as a short-lived container apart from the API; it carries no
# secrets and is given nothing but the package address.
FROM node:20-bookworm-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-venv python3-pip curl unzip ca-certificates \
 && rm -rf /var/lib/apt/lists/*

COPY run.sh /opt/run.sh
COPY preview_main.py /opt/preview_main.py
COPY preview.css /opt/preview.css
RUN sed -i 's/\r$//' /opt/run.sh /opt/preview_main.py && chmod +x /opt/run.sh

ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["/opt/run.sh"]
