# Guía de configuración de ResourceAlerter

Toda la configuración se hace desde el Viewer (`ResourceAlerter Viewer`, acceso directo en el
escritorio) → botón **Configuración**, arriba a la derecha. No hace falta editar ningún archivo
JSON a mano.

Si `appsettings.json` todavía no existe en el servidor, la pantalla arranca con los mismos
valores por defecto que usaría el servicio — no hace falta "crear" nada antes de configurar.

Al guardar, te ofrece **reiniciar el servicio** (pide elevación de Windows) para que los cambios
se apliquen al toque. Si decís que no, los cambios quedan guardados igual, pero no se van a
aplicar hasta el próximo reinicio del servicio.

---

## General

| Campo | Qué hace |
|---|---|
| Nombre de máquina (mails/logs) | El nombre que aparece en el asunto y cuerpo de los mails (`[NombreMáquina] ALERT: ...`) y en los logs. Vacío = usa el nombre real de Windows. **No afecta** el archivo `appsettings.<NOMBRE-REAL-WINDOWS>.json` de overrides por servidor — ese siempre usa el nombre real del sistema operativo, sin importar lo que pongas acá. |
| Intervalo de sondeo (segundos) | Cada cuánto se revisan todos los monitores (CPU, memoria, disco, etc). Default: 10 segundos. |

---

## SMTP

Los datos de tu servidor de correo o relay. Todos los campos son los típicos de cualquier
cliente de mail:

| Campo | Notas |
|---|---|
| Servidor (Host) | La dirección de tu relay/servidor SMTP. |
| Puerto | 25 sin cifrado, 587 con STARTTLS (lo más común hoy), 465 con SSL directo. |
| Usar SSL/TLS | Depende de tu relay. Si usás el puerto 587 y tu relay lo requiere, probá activarlo. |
| Requiere autenticación | Si tu relay pide usuario/contraseña. Muchos relays internos no la piden. |
| Usuario / Contraseña | Solo se usan si "Requiere autenticación" está tildado. |
| Dirección remitente | El "De:" que va a aparecer en los mails. |
| Nombre remitente | El nombre visible junto al remitente (ej: "ResourceAlerter"). |
| Destinatarios | Uno por línea. Podés poner varios. |
| Reintentos de envío | Si falla el envío, cuántas veces reintenta antes de darse por vencido. |
| Espera entre reintentos | Segundos entre cada reintento. |
| Timeout | Cuánto espera antes de considerar que el envío falló. Los mails con adjuntos (resumen diario) usan automáticamente un timeout más largo internamente, no hace falta subir este valor por eso. |

**Si un mail falla**, revisá el log (`logs\resourcealerter-*.log`, carpeta de instalación) — ahí
queda el detalle exacto del error de SMTP.

---

## Discord

Alerta opcional en paralelo al mail, vía webhook de Discord (no hace falta bot).

| Campo | Notas |
|---|---|
| Habilitado | Si está destildado, esta sección se ignora por completo. |
| URL del webhook | Ver más abajo cómo conseguirla. |

### Cómo conseguir la URL del webhook

1. En Discord, andá al canal donde querés recibir las alertas.
2. Click derecho sobre el canal (o el ⚙️) → **Editar canal**.
3. **Integraciones** → **Webhooks** → **Crear webhook** (o "Nuevo Webhook").
4. Ponele un nombre (ej. "ResourceAlerter").
5. **Copiar URL del webhook** — esa es la URL que va en este campo. Tiene el formato
   `https://discord.com/api/webhooks/{id}/{token}`.
6. Guardá los cambios del webhook en Discord.

Ni el link del canal (`discord.com/channels/...`) ni un link de invitación (`discord.gg/...`)
sirven acá — tiene que ser específicamente la URL del webhook.

Discord recibe el aviso como un mensaje formateado (embed), sin adjuntos — gráficos, logs y el
reporte de hardware siguen yendo solo por mail.

---

## Monitores: lógica común

Todos los monitores (CPU, Memoria, Disco, Temperatura, Voltaje, Red) comparten esta lógica
anti-spam, configurable individualmente en cada pestaña:

- **Ventana sostenida (segundos)**: un valor fuera de rango tiene que mantenerse así por esta
  cantidad de segundos antes de considerarse una alerta real — un pico momentáneo no dispara nada.
- **Ventana de recuperación (segundos)**: una vez que el valor vuelve a rango normal, tiene que
  mantenerse así por esta cantidad de segundos antes de mandar el mail de "resuelto".
- **Recordatorio cada (minutos)**: mientras la alerta sigue activa, cada cuánto se reenvía un
  mail de recordatorio (en vez de mandar uno por cada ciclo de sondeo).

---

## CPU

| Campo | Notas |
|---|---|
| Umbral de alerta (%) | Uso de CPU sostenido por encima de este valor dispara la alerta. |
| Umbral de recuperación (%) | Tiene que bajar de **este** valor (más bajo que el de alerta) para considerarse resuelto — evita que el aviso "parpadee" si el uso oscila justo en el límite. |

## Memoria

Igual que CPU, pero sobre % de RAM física usada.

## Disco

| Campo | Notas |
|---|---|
| Umbral de espacio libre (%) | Alerta si el espacio libre baja de este porcentaje. |
| Umbral de espacio libre (GB) | Alerta si el espacio libre baja de esta cantidad absoluta. Se dispara con **cualquiera** de los dos umbrales que se cumpla primero. |
| Unidades a vigilar | **Vacío = solo la unidad de sistema** (normalmente `C:`). Si escribís una o más acá, **reemplazan** a la de sistema, no se suman. Por ejemplo: poné solo `D:` para vigilar la unidad de temporales/swap en vez de C:, o `C:, D:` para vigilar ambas. |

## Temperatura

| Campo | Notas |
|---|---|
| Umbral de alerta (°C) | Temperatura de CPU sostenida por encima de este valor. |

Si tu hardware no expone sensor de temperatura, el monitor se ignora en silencio (sin mail) y
queda listado como "no monitoreado" en el mail de arranque del servicio — no es un error.

## Voltaje

| Campo | Notas |
|---|---|
| Desviación permitida (%) | Cuánto puede desviarse un riel de su voltaje nominal antes de alertar (ej: 5% sobre 12V = alerta si sale de 11.4V–12.6V). |
| Rieles nominales | Uno por línea, formato `Nombre=Voltios` (ej: `+12V=12`). Son los rieles de la fuente que se vigilan. |
| Nombres de sensor reales | Opcional. Si un riel no matchea automáticamente con el sensor real de tu placa, poné acá el nombre exacto que ves con `--list-sensors` (ver abajo). Formato `Riel=NombreSensor` (ej: `+3.3V=AVCC3`). |

### Cómo ver los nombres reales de los sensores de tu placa

Cada fabricante de placa nombra los sensores distinto. Para verlos:

1. Abrí una PowerShell **como Administrador**.
2. Andá a la carpeta de instalación (`C:\Program Files\ResourceAlerter`).
3. Corré: `.\ResourceAlerter.exe --list-sensors`
4. Vas a ver una lista de todos los sensores de temperatura y voltaje que expone tu hardware,
   con sus nombres reales.

Si un riel (por ejemplo `+3.3V`) no aparece automáticamente monitoreado pero ves en la lista un
sensor con otro nombre que corresponde a ese voltaje (por ejemplo `AVCC3`), agregalo en "Nombres
de sensor reales" como se explica arriba.

Algunas placas simplemente no exponen ciertos rieles (el standby de 5V es común que falte) — en
ese caso no hay nada para configurar, es una limitación del hardware/chip.

## Red

| Campo | Notas |
|---|---|
| Host a pingear | Vacío = detecta automáticamente el gateway de la red. Si querés pingear algo específico (ej. un servidor central), ponelo acá. |
| Host de respaldo | Se usa si no se puede detectar el gateway automáticamente. Default: `8.8.8.8`. |
| Intervalo de ping | Cada cuántos segundos se hace un ping. |
| Timeout de ping | Cuánto espera una respuesta antes de considerarlo una pérdida. |
| Tamaño de ventana | Cuántos pings recientes se tienen en cuenta para calcular pérdidas. |
| Máx. pérdidas en la ventana | Si se superan estas pérdidas dentro de la ventana, alerta. |
| Máx. corte continuo (segundos) | Si no hay respuesta durante más de estos segundos seguidos, alerta (aunque no se haya superado el máximo de pérdidas). |

---

## Base de datos

| Campo | Notas |
|---|---|
| Ruta del archivo SQLite | Dónde se guardan los datos históricos. Por defecto en `%ProgramData%\ResourceAlerter\` (no en la carpeta de instalación) para que el Viewer pueda leerla sin necesitar permisos de administrador. No suele hacer falta cambiar esto. |
| Retención de datos (días) | Cuántos días de historial se guardan antes de purgarse automáticamente. Default: 90. |

## Logs

| Campo | Notas |
|---|---|
| Carpeta de logs | Dónde se escriben los archivos de log de texto. |
| Tamaño máximo por archivo (MB) | Si un log del día supera este tamaño, se parte en un archivo nuevo. |
| Retención de logs (días) | Cuántos días de logs se conservan antes de borrarse. |

---

## Preguntas frecuentes

**¿Necesito reiniciar el servicio después de guardar?**
Sí, para que tome los cambios. La pantalla te lo ofrece automáticamente al guardar (con
elevación). Si decís que no en ese momento, podés reiniciarlo después manualmente desde
`services.msc` o con `Restart-Service ResourceAlerter` en una PowerShell elevada.

**Edité `appsettings.json` a mano en vez de usar la pantalla, ¿se pierde con una actualización?**
No — a partir de la v2.0.4, `appsettings.json` nunca es tocado por el instalador, sea como sea
que lo hayas creado (a mano o desde la pantalla de Configuración).

**¿Puedo copiar la configuración de un servidor a otro?**
Sí, `appsettings.json` es un archivo de texto normal — podés copiarlo entre servidores y después
ajustar lo que cambie (nombre de máquina, unidades de disco, rieles de voltaje) desde la pantalla
de Configuración.
