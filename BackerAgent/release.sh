#!/bin/bash
rm -rf bin && dotnet build -c Release --self-contained && dotnet publish -c Release --self-contained
