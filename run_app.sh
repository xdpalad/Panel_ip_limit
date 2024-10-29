#!/bin/bash

# Перейти в директорию, где находится ваше приложение
cd /root/app

# Запустить приложение с nohup, чтобы оно продолжило работать в фоне после выхода из терминала
nohup ./MyApp > myapp.log 2>&1 &

# Сохранить PID процесса в файл для возможного завершения его позже
echo $! > myapp.pid

echo "MyApp is running in the background with PID $(cat myapp.pid)"
