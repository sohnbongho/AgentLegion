#!/bin/bash

#stop
#./STOP.sh

#remove core
#rm core.*

#session
./sessionServer -d 0 --logoutput=console wind1000 > ./logs/log.session 2>&1 & 

#wind
./wind wind0 > ./logs/log.wind0 2>&1 & #master
./wind wind1 > ./logs/log.wind1 2>&1 & #sync
./wind wind2 > ./logs/log.wind2 2>&1 & #login
./wind wind11 > ./logs/log.wind11 2>&1 & #local
