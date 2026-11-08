# Bienvenido a Amoxtli

Un lugar tranquilo para escribir y leer tus archivos **Markdown**, con controles nativos de Windows.

Abre un archivo con **Ctrl+O**, una carpeta con **Ctrl+Mayús+O** o arrástralos a esta ventana. En el panel **Contenido** puedes saltar entre los encabezados del documento.

El primer botón de la barra de herramientas muestra u oculta el panel lateral. También puedes usar **Ctrl+Mayús+L**.

> Cambia entre **Fuente**, **Formato** y **Dividido** junto al botón Abrir. En Fuente escribes Markdown; en Formato editas el documento como se ve; en Dividido haces las dos cosas a la vez, con el scroll sincronizado. Guarda con **Ctrl+S**. En una carpeta que abriste, los cambios se guardan solos.

## Todo a mano

| Acción | Atajo |
| :--- | :--- |
| Abrir archivo | Ctrl+O |
| Abrir carpeta | Ctrl+Mayús+O |
| Buscar | Ctrl+F |
| Copiar selección | Ctrl+C |
| Copiar Markdown completo | Ctrl+Mayús+C |
| Guardar / Guardar como | Ctrl+S / Ctrl+Mayús+S |
| Deshacer / Rehacer | Ctrl+Z / Ctrl+Y |
| Fuente / Formato / Dividido | Ctrl+Mayús+1 / 2 / 3 |
| Negrita / Cursiva / Subrayado | Ctrl+B / Ctrl+I / Ctrl+U |
| Título 1 a 6 / Texto | Ctrl+1 … Ctrl+6 / Ctrl+7 |
| Enlace | Ctrl+K |
| Tamaño del texto | Ctrl+= / Ctrl+- / Ctrl+0 |
| Mostrar u ocultar el panel | Ctrl+Mayús+L |
| Recargar archivo y carpeta | F5 |
| Atrás / Adelante | Alt+← / Alt+→ |

## Formato de texto

Escribe texto en **negrita**, *cursiva*, ***ambas***, <u>subrayado</u> y ~~tachado~~, y `código en línea`. Amoxtli también lee ==resaltado==, subíndices como H~2~O y superíndices como x^2^, y los conserva tal como están escritos.

### Listas y tareas

- Listas con viñetas
  - Elementos anidados
  - Párrafos y enlaces dentro de cada elemento
- [x] Leer y editar documentos
- [ ] Explorar tu siguiente idea

1. Abre una carpeta.
2. Elige un archivo de la barra lateral.
3. Navega por su contenido.

### Código

Los bloques de código se colorean según su lenguaje: Swift, Python, JavaScript, TypeScript, JSON, HTML/XML, CSS, SQL y Shell. En Formato, el botón de la esquina de cada bloque cambia el lenguaje.

```js
// Selecciona el texto o usa el botón Copiar.
const saludo = "Hola, mundo";
console.log(saludo);
```

## Enlaces e imágenes

Los [enlaces web](https://www.markdownguide.org/) se abren en tu navegador predeterminado. Haz clic derecho sobre un enlace para **copiarlo** o usar **Abrir con…** y elegir otra aplicación mediante Windows.

Los enlaces a otros archivos Markdown se abren aquí. Los enlaces `#encabezado` navegan dentro del documento.

Puedes cargar imágenes locales relativas al archivo e imágenes HTTP/HTTPS. Activa o desactiva las imágenes remotas desde **Preferencias**. Los archivos pegados no tienen una carpeta base para imágenes relativas.

```markdown
![Descripción de la imagen](imagenes/captura.png)
![Imagen remota](https://ejemplo.com/imagen.png)
```

## Más posibilidades

Las tablas, las citas anidadas, los separadores, las entidades HTML, las listas de definiciones y las notas al pie[^nota] también tienen presentación nativa.

Markdown

:   Un formato de texto sencillo y legible.

WinUI

:   La biblioteca de interfaz nativa de Windows usada por Amoxtli.

---

El HTML sin procesar se muestra como código, sin ejecutar scripts. Las fórmulas LaTeX y los diagramas Mermaid se conservan como texto o bloques de código; no se dibujan como fórmulas o diagramas.

[^nota]: Haz clic en el número de una nota para leerla y usa su flecha para volver.

## Tu app predeterminada

Después de instalar Amoxtli como paquete MSIX, ve a **Archivo → Elegir como app predeterminada…** y asigna las extensiones `.md`, `.markdown`, `.mdown`, `.mkd` o `.mkdn` en Configuración de Windows.

Si Amoxtli aún no es la app predeterminada para `.md`, te ofrecerá abrir esa configuración al iniciar. Puedes elegir **Ahora no** y marcar **No volver a preguntar** para recordar tu respuesta. El aviso se puede reactivar en **Preferencias**.
