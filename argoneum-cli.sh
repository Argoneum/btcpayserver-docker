#!/bin/bash

docker exec btcpayserver_argoneumd argoneum-cli -datadir="/data" "$@"
