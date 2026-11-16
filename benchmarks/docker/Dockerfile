# Every toolchain the benchmarks need, at the versions used on Windows. Build from the repository root:
#   docker build -f benchmarks/docker/Dockerfile -t z3l-bench .
#   docker run --rm --cpuset-cpus 8-15 --memory 12g z3l-bench
# The table is printed at the end and written to benchmarks/results-linux.md inside the container.
FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-venv build-essential pkg-config libssl-dev clang zlib1g-dev curl ca-certificates xz-utils \
 && rm -rf /var/lib/apt/lists/*

ARG NODE=26.13.1
ARG GO=1.27.4
ARG JDK=21.0.13_9
ARG MAVEN=3.10.1

RUN curl -fsSL https://nodejs.org/dist/v${NODE}/node-v${NODE}-linux-x64.tar.xz | tar -xJ -C /opt \
 && mv /opt/node-v${NODE}-linux-x64 /opt/node
RUN curl -fsSL https://go.dev/dl/go${GO}.linux-amd64.tar.gz | tar -xz -C /opt
RUN curl -fsSL "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-$(echo ${JDK} | tr _ +)/OpenJDK21U-jdk_x64_linux_hotspot_${JDK}.tar.gz" | tar -xz -C /opt \
 && mv /opt/jdk-* /opt/jdk
RUN curl -fsSL https://dlcdn.apache.org/maven/maven-3/${MAVEN}/binaries/apache-maven-${MAVEN}-bin.tar.gz | tar -xz -C /opt \
 && mv /opt/apache-maven-${MAVEN} /opt/maven
RUN curl -fsSL https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain stable

ENV JAVA_HOME=/opt/jdk \
    PATH=/opt/node/bin:/opt/go/bin:/opt/jdk/bin:/opt/maven/bin:/root/.cargo/bin:$PATH \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

WORKDIR /work
COPY benchmarks/python/requirements.txt benchmarks/python/requirements.txt
RUN python3 -m venv /venv && /venv/bin/pip install -q -r benchmarks/python/requirements.txt

COPY . .
RUN /venv/bin/python benchmarks/run.py --build-only

CMD ["/venv/bin/python", "-u", "benchmarks/run.py", "--out", "results-linux.md"]
