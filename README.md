# Amoxtli

Visor de Markdown de solo lectura para Windows, escrito en C# y **WinUI 3**. Tanto la interfaz como el documento usan controles nativos (`RichTextBlock`, `Grid`, `TreeView`, etc.); no se utiliza WebView para presentar el contenido.

## Funciones

- Abrir archivos y carpetas, arrastrarlos a la ventana y recorrer subcarpetas bajo demanda.
- Extensiones `.md`, `.markdown`, `.mdown`, `.mkd` y `.mkdn`.
- Índice de encabezados con anclas, enlaces entre documentos e historial atrás/adelante.
- CommonMark y extensiones: negritas, cursivas, tachado, subrayado, marcas de énfasis, subíndices, superíndices, listas anidadas, tareas de solo lectura, citas, separadores, código, tablas pipe/grid con alineación, notas al pie, definiciones, abreviaciones y contenedores.
- Imágenes locales y HTTP/HTTPS, incluidas imágenes SVG compatibles con WinUI. Las imágenes remotas se pueden desactivar desde las preferencias.
- Seleccionar y copiar texto con los controles nativos, copiar el Markdown completo y pegar Markdown o archivos del portapapeles para leerlos. Pegar no modifica el archivo abierto.
- Los enlaces web se abren en el navegador predeterminado. Su menú de clic derecho permite copiar la dirección y solicitar a Windows **Abrir con…** (`LauncherOptions.DisplayApplicationPicker`). La lista disponible depende de las aplicaciones registradas en Windows.
- Búsqueda por bloques con resaltado y navegación, vista de código fuente, tamaño de texto, tema de Windows/claro/oscuro y progreso de lectura.
- Recarga automática del archivo abierto cuando cambia en disco y recarga manual con F5.
- Botón en la barra de herramientas para mostrar u ocultar el panel de navegación, también con Ctrl+B.

## Ejecutar

Requiere Windows 10 1809 o superior, .NET SDK con soporte para el destino .NET 8 y las herramientas de desarrollo WinUI/Windows SDK. El proyecto conserva las versiones de Windows App SDK y BuildTools de la plantilla original y añade Markdig 0.41.3.

En Visual Studio, abre `Amoxtli.slnx`, selecciona **x64** y el perfil **Amoxtli (Package)** para compilar, registrar y ejecutar el paquete de desarrollo.

Para una ejecución sin instalar:

```powershell
.\scripts\Run-Dev.ps1
```

El script compila una versión x64 autocontenida en `artifacts\dev` y abre la aplicación. También puedes abrir archivos desde la línea de comandos:

```powershell
.\artifacts\dev\Amoxtli.exe "C:\Documentos\README.md"
.\artifacts\dev\Amoxtli.exe "C:\Documentos\notas"
```

## App predeterminada de Markdown

`Package.appxmanifest` declara las asociaciones de las cinco extensiones. **La asociación requiere instalar o registrar el paquete MSIX**; ejecutar el `.exe` sin paquete no registra extensiones.

Después de instalarlo, usa **Archivo → Elegir como app predeterminada…**, busca Amoxtli o cada extensión en Configuración de Windows y elige la aplicación. También puedes usar **Abrir con → Elegir otra aplicación** desde el Explorador. Windows conserva el control de esta elección; la app no cambia asociaciones en el registro.

Al iniciar la versión instalada, Amoxtli consulta la asociación de `.md` y ofrece abrir Configuración si aún no es la app predeterminada. Marca **No volver a preguntar** para recordar tu respuesta, tanto si eliges **Ahora no** como si abres Configuración. Puedes reactivar el aviso en **Preferencias de lectura**. En Windows 11 compatible, el botón abre directamente la página de Amoxtli; en Windows 10 abre Aplicaciones predeterminadas. Las ejecuciones sin paquete no muestran este aviso.

Para generar un paquete sin firma, que después debes firmar con tu certificado de distribución:

```powershell
dotnet build Amoxtli.csproj -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -p:AppxBundle=Never
```

## Validación

```powershell
.\scripts\Test-Smoke.ps1
```

Estas pruebas ejecutan la aplicación en un dispatcher real de WinUI y revisan el análisis de Markdown, la distribución nativa de las tablas, las tareas, el índice, las anclas, las notas, la búsqueda, el historial, los errores de archivo y la apertura de carpetas. El informe se guarda en `artifacts\smoke-results.json`. No usan el portapapeles del usuario ni abren navegadores, cambian asociaciones o guardan preferencias. El modo de pruebas solo existe en Debug.

## Límites de esta versión

- El HTML sin procesar se conserva como código fuente; `<br>` se interpreta como salto de línea. No se ejecuta JavaScript ni se incrustan páginas web.
- Las fórmulas LaTeX y los diagramas Mermaid se conservan como texto/código. No se presentan gráficamente.
- No incluye coloreado sintáctico de código. La marca `==texto==` usa el color de énfasis del sistema.
- Los documentos tienen un límite de 8 MB; las imágenes, de 15 MB cada una. Se cargan hasta cuatro imágenes simultáneamente. Los formatos de imagen y las funciones SVG dependen de WinUI/Windows.
- La selección nativa funciona dentro de cada bloque. Para copiar todo el documento usa **Copiar Markdown completo** (`Ctrl+Mayús+C`).
- El árbol omite archivos/carpetas ocultos, del sistema y puntos de reanálisis. F5 actualiza la lista de archivos. Los documentos abiertos desde el Explorador crean ventanas independientes.
- Las preferencias se guardan en la carpeta local de datos de la aplicación, en `Amoxtli\settings.json`.

La guía incorporada está en `Assets\Welcome.md`. El parser Markdig se distribuye bajo BSD-2-Clause; consulta `THIRD-PARTY-NOTICES.md`.

El icono combina el símbolo de Markdown con un códice plegado de inspiración mesoamericana. El original transparente y el prompt de ImageGen están en `Assets\Branding`; `scripts\Build-IconAssets.ps1` genera el ICO y los recursos de Windows. El icono está configurado para el ejecutable, la ventana, el paquete y las asociaciones de archivos.
