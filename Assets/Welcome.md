# Bienvenido a Amoxtli

Un lugar tranquilo para leer tus archivos **Markdown**, con controles nativos de Windows.

Abre un archivo con **Ctrl+O**, una carpeta con **Ctrl+Mayús+O** o arrástralos a esta ventana. En el panel **Contenido** puedes saltar entre los encabezados del documento.

El primer botón de la barra de herramientas muestra u oculta el panel lateral. También puedes usar **Ctrl+B**.

> Amoxtli es un visor de solo lectura. Puedes seleccionar y copiar texto, o pegar Markdown con **Ctrl+V** para leerlo sin modificar ningún archivo.

## Todo a mano

| Acción | Atajo |
| :--- | :--- |
| Abrir archivo | Ctrl+O |
| Abrir carpeta | Ctrl+Mayús+O |
| Buscar | Ctrl+F |
| Copiar selección | Ctrl+C |
| Copiar Markdown completo | Ctrl+Mayús+C |
| Pegar para leer | Ctrl+V |
| Mostrar código fuente | Ctrl+U |
| Mostrar u ocultar el panel | Ctrl+B |
| Recargar archivo y carpeta | F5 |
| Atrás / Adelante | Alt+← / Alt+→ |

## Formato de texto

Lee texto en **negrita**, *cursiva*, ***ambas***, ~~tachado~~, ++subrayado++ y ==resaltado==. También hay `código en línea`, subíndices como H~2~O y superíndices como x^2^.

### Listas y tareas

- Listas con viñetas
  - Elementos anidados
  - Párrafos y enlaces dentro de cada elemento
- [x] Leer documentos sin editarlos
- [ ] Explorar tu siguiente idea

1. Abre una carpeta.
2. Elige un archivo de la barra lateral.
3. Navega por su contenido.

### Código

```csharp
// Selecciona el texto o usa el botón Copiar.
var saludo = "Hola, mundo";
Console.WriteLine(saludo);
```

## Enlaces e imágenes

Los [enlaces web](https://www.markdownguide.org/) se abren en tu navegador predeterminado. Haz clic derecho sobre un enlace para **copiarlo** o usar **Abrir con…** y elegir otra aplicación mediante Windows.

Los enlaces a otros archivos Markdown se abren aquí. Los enlaces `#encabezado` navegan dentro del documento.

Puedes cargar imágenes locales relativas al archivo e imágenes HTTP/HTTPS. Activa o desactiva las imágenes remotas desde **Preferencias de lectura**. Los archivos pegados no tienen una carpeta base para imágenes relativas.

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

Si Amoxtli aún no es la app predeterminada para `.md`, te ofrecerá abrir esa configuración al iniciar. Puedes elegir **Ahora no** y marcar **No volver a preguntar** para recordar tu respuesta. El aviso se puede reactivar en **Preferencias de lectura**.
