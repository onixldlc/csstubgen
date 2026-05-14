#!/bin/bash

go build -o csstubgen ./main.go
mv csstubgen ~/coding/linux-utilities/bin/csstubgen
csstubgen build
