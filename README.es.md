# KodaMemo

Graba conversaciones y conserva lo importante.

Aplicación Android con un espacio web complementario para organizar transcripciones, acuerdos, pendientes y mapas mentales. Koda es el asistente.

**Estado: alfa para un solo propietario y desarrollo local.** La grabación web es una simulación identificada en pantalla. El asistente actual funciona con reglas. Las API de IA de pago y la interacción por voz están pendientes.

Android permite grabar, pausar, continuar, reproducir y recuperar archivos WAV interrumpidos. La transcripción local utiliza Whisper.net y necesita un modelo externo. La web conserva reuniones en SQLite y permite consultar y editar información sincronizada.

- [Instalación y configuración](docs/SETUP.md)
- [Arquitectura y límites](docs/ARCHITECTURE.md)
- [Contribuciones](CONTRIBUTING.md)
- [Seguridad](SECURITY.md)

No se incluyen grabaciones, bases de datos, modelos, contraseñas ni claves de firma. Antes de una versión estable falta migrar los SDK heredados a versiones estables soportadas, probar Android en dispositivos físicos y completar las funciones pendientes.
