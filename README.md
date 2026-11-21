# TOTP Everywhere

Кроссплатформенное приложение на .NET 10 и Avalonia UI для Windows, macOS, Linux, iOS и Web. Создаёт шестизначные и восьмизначные коды по RFC 6238 и показывает оставшееся время действия каждого кода.

## Структура

| Проект | Назначение |
| --- | --- |
| `Totp.Core` | TOTP, разбор данных QR и шифрование хранилища |
| `Totp.App` | Общий интерфейс Avalonia и обработка изображений |
| `Totp.Desktop`, `Totp.iOS`, `Totp.Browser` | Запуск приложения и подключение камеры и хранилища |
| `Totp.Tests` | xUnit-тесты TOTP, импорта и шифрования |

## Запуск

Установите [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Выполняйте команды из корня репозитория. Полное решение `TotpEverywhere.slnx` включает iOS и требует macOS, Xcode и оба workload.

### Windows, macOS и Linux

```sh
dotnet restore src/Totp.Desktop
dotnet build src/Totp.Desktop --no-restore
dotnet run --project src/Totp.Desktop --no-build
```

Для камеры установите [FFmpeg](https://ffmpeg.org/download.html) и добавьте его в `PATH`. Поместите QR-код в кадр и нажмите **Камера**. Приложение сделает снимок примерно через секунду.

| ОС | Выбор устройства |
| --- | --- |
| Windows | Выполните `ffmpeg -list_devices true -f dshow -i dummy`, затем задайте точное имя устройства в PowerShell, например `$env:TOTP_CAMERA = 'Integrated Camera'`. |
| macOS | По умолчанию используется устройство с индексом 0. Для другого устройства выполните `ffmpeg -f avfoundation -list_devices true -i ""` и задайте индекс, например `export TOTP_CAMERA=1`. При запросе macOS разрешите доступ к камере. |
| Linux | По умолчанию используется `/dev/video0`. Другую камеру можно выбрать через `export TOTP_CAMERA=/dev/video2`. |

### Web

```sh
dotnet workload install wasm-tools
dotnet restore src/Totp.Browser
dotnet build src/Totp.Browser --no-restore
dotnet run --project src/Totp.Browser --no-build
```

Откройте адрес, выведенный в терминале. Используйте актуальный браузер с WebAssembly и Web Locks. Для доступа к камере и Web Locks нужен HTTPS или localhost. Во вкладке камеры наведите её на QR и нажмите **Снять QR**.

### iOS

На macOS установите Xcode и совместимый с ним workload iOS. Чтобы запустить симулятор, выберите установленное устройство в приложении Simulator и запустите проект из IDE или терминала. На Apple Silicon:

```sh
dotnet workload install ios
dotnet restore src/Totp.iOS -r iossimulator-arm64
dotnet build src/Totp.iOS -r iossimulator-arm64 --no-restore
dotnet build src/Totp.iOS -t:Run -r iossimulator-arm64 --no-restore
```

Для Intel Mac используйте `iossimulator-x64`. Для камеры требуется физическое устройство с разрешением на съёмку и подписанной сборкой `ios-arm64`. Загрузку изображений можно использовать и в симуляторе.

## Импорт и пароль

1. Введите логин и пароль длиной от 12 символов. При первом запуске нажмите **Регистрация**, при следующих запусках нажимайте **Войти**.
2. В Google Authenticator откройте **Перенос аккаунтов → Экспорт аккаунтов**. Отсканируйте QR с другого экрана через **Камера** или загрузите его изображение через **Загрузить QR**. Если кодов несколько, загрузите все из одного экспорта. Приложение также читает обычные QR `otpauth://totp`. Записи HOTP пропускаются.
3. Выберите **Сменить пароль**, введите прежний пароль и дважды новый. Хранилище будет перешифровано с новым ключом.

Данные хранятся локально и привязаны к профилю. Desktop и iOS используют каталог `TotpEverywhere` внутри `LocalApplicationData`. Web использует локальное хранилище текущего сайта и профиля браузера. Резервной синхронизации и восстановления пароля нет. При очистке данных сайта браузер удалит хранилище.

Логин и пароль участвуют в выводе ключа PBKDF2-HMAC-SHA256 с 600 000 итераций и случайной солью. Записи шифруются AES-256-GCM. При каждой записи генерируется новый nonce. Хэш нормализованного логина используется как идентификатор профиля.

## Тесты

```sh
dotnet restore tests/Totp.Tests
dotnet build tests/Totp.Tests --no-restore
dotnet test tests/Totp.Tests --no-build --no-restore
```

GitHub Actions собирает Desktop на Windows, macOS и Linux, собирает Web и iOS Simulator и запускает xUnit-тесты на трёх Desktop-платформах.
