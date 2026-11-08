# Amoxtli

Editor y visor de Markdown para Windows, escrito en C# y **WinUI 3**. Tanto la interfaz como el documento usan controles nativos (`RichEditBox`, `RichTextBlock`, `Grid`, `TreeView`, etc.); no se utiliza WebView para presentar ni editar el contenido.

Licencia [MIT](LICENSE) · [Privacidad en Windows](PRIVACY.md) · [Code signing policy](CODE_SIGNING.md)

## Funciones

### Edición

- Tres modos por ventana, junto a **Abrir**: **Fuente** (Markdown con resaltado), **Formato** (el documento tal como se ve, editable) y **Dividido** (los dos a la vez, sincronizados). También en **Ver**, con Ctrl+Mayús+1, 2 y 3.
- **Fuente**: resaltado incremental de títulos, estilos, enlaces, citas, listas y código; Return continúa listas y citas; sin correcciones automáticas.
- **Formato**: un bloque nativo por párrafo, código y celda. Viñetas, números, casillas de tareas y barras de cita se dibujan aparte; Return, Borrar y Tab funcionan como en Pages; `# ` tecleado crea un título; las tablas dibujan su cuadrícula completa. Ctrl+clic abre un enlace.
- **Dividido**: los dos editores escriben el mismo Markdown; el scroll se empareja bloque por bloque.
- Herramientas de formato en la barra (que se desborda una por una), en el menú **Formato** y en el clic derecho: estilos de párrafo, negrita, cursiva, subrayado, tachado, código, cita, listas, enlace, bloque de código, tabla y línea divisoria. Muestran lo que aplica en el cursor.
- Bloques de código coloreados por lenguaje (Swift, Python, JavaScript, TypeScript, JSON, HTML/XML, CSS, SQL y Shell) en ambos editores. En Formato, el botón de cada bloque cambia su lenguaje.
- Tipografía global: Sistema, Serif o Monoespaciada, y tamaño por pasos de 10 a 32 (Ctrl+=, Ctrl+-, Ctrl+0 y el menú **Aa**).
- Guardado con Ctrl+S y Guardar como; aviso de cambios sin guardar al cambiar de documento o cerrar. En una carpeta abierta con **Abrir carpeta**, los cambios se guardan solos un segundo después de editar, al cambiar de archivo, al salir de la app y al cerrar.
- Escritura atómica que conserva la codificación, el BOM y los finales de línea del archivo. Abrir o leer nunca reescribe el archivo; un archivo que no es UTF-8 válido no se muestra ni se sobrescribe.
- Si el archivo cambia en otra app, se recarga cuando no hay cambios sin guardar; si los hay, Amoxtli pregunta qué versión conservar y nunca guarda encima sin preguntar. Si se borra, el texto se conserva.
- La primera edición en Formato deja el archivo en forma canónica. Lo que el editor no modela (front matter, HTML, notas al pie, listas de definiciones, `==marcas==`) se conserva tal como está escrito.

### Lectura

- Abrir archivos y carpetas, arrastrarlos a la ventana y recorrer subcarpetas bajo demanda.
- Extensiones `.md`, `.markdown`, `.mdown`, `.mkd` y `.mkdn`.
- Índice de encabezados con anclas, enlaces entre documentos e historial atrás/adelante.
- CommonMark y extensiones: negritas, cursivas, tachado, subrayado, marcas de énfasis, subíndices, superíndices, listas anidadas, tareas, citas, separadores, código, tablas pipe/grid con alineación, notas al pie, definiciones, abreviaciones y contenedores.
- Imágenes locales y HTTP/HTTPS, incluidas imágenes SVG compatibles con WinUI. Las imágenes remotas se pueden desactivar desde las preferencias.
- Los enlaces web se abren en el navegador predeterminado. Su menú de clic derecho permite copiar la dirección y solicitar a Windows **Abrir con…** (`LauncherOptions.DisplayApplicationPicker`).
- Búsqueda con navegación entre coincidencias, palabras y tiempo de lectura en una franja bajo el documento, tema de Windows/claro/oscuro y progreso de lectura.
- Interfaz y guía de bienvenida en español, inglés, portugués, francés, alemán, italiano, ruso, chino simplificado, japonés, coreano, árabe e hindi. El idioma de Windows se detecta automáticamente, con inglés como alternativa. La interfaz se adapta de derecha a izquierda para árabe; los documentos y el código conservan su dirección.

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

`Package.appxmanifest` declara las asociaciones de las cinco extensiones para Microsoft Store/MSIX. El instalador de GitHub también puede registrar esas extensiones en **Abrir con** y en Aplicaciones predeterminadas, sin sustituir la elección del usuario. Ejecutar el `.exe` portable sin instalar no registra extensiones.

Después de instalarlo, usa **Archivo → Elegir como app predeterminada…**, busca Amoxtli o cada extensión en Configuración de Windows y elige la aplicación. También puedes usar **Abrir con → Elegir otra aplicación** desde el Explorador. Windows conserva el control de esta elección; la app no cambia asociaciones en el registro.

Al iniciar la versión instalada, Amoxtli consulta la asociación de `.md` y ofrece abrir Configuración si aún no es la app predeterminada. Marca **No volver a preguntar** para recordar tu respuesta, tanto si eliges **Ahora no** como si abres Configuración. Puedes reactivar el aviso en **Preferencias de lectura**. En Windows 11 compatible, el botón abre directamente la página de Amoxtli; en Windows 10 abre Aplicaciones predeterminadas. Las ejecuciones sin paquete no muestran este aviso.

Para generar un paquete sin firma, que después debes firmar con tu certificado de distribución:

```powershell
dotnet build Amoxtli.csproj -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -p:AppxBundle=Never
```

## Instalador para GitHub Releases

Para generar el instalador x64 necesitas el SDK de .NET y las herramientas de Windows indicadas arriba, además de [Inno Setup 6.3 o posterior](https://jrsoftware.org/isdl.php). El compilador se detecta en las rutas habituales, en `PATH` o en `artifacts\tools\innosetup`; también puedes indicar su ruta con `-CompilerPath`.

```powershell
.\scripts\Build-Installer.ps1
# Para publicar otra versión:
.\scripts\Build-Installer.ps1 -Version 1.0.1
```

Sin `-Version`, usa los tres primeros componentes de la versión de `Package.appxmanifest`. Genera estos dos archivos para adjuntarlos a la release correspondiente:

- `artifacts\releases\Amoxtli-1.0.0-windows-x64-setup.exe`
- `artifacts\releases\Amoxtli-1.0.0-windows-x64-setup.exe.sha256`

El instalador funciona en Windows 10 1809 o posterior compatible con x64, instala para el usuario actual en `%LOCALAPPDATA%\Programs\Amoxtli` y añade un acceso al menú Inicio y un desinstalador. El acceso al escritorio es opcional. La interfaz del instalador está disponible en español e inglés; la app conserva sus 12 idiomas.

La distribución incluye .NET y Windows App SDK, así que no necesita Microsoft Store ni la instalación previa de esos runtimes. Usa una publicación Release sin recorte ni empaquetado MSIX; el proceso de Store queda desactivado solo para esta compilación. Las actualizaciones se instalan ejecutando el nuevo instalador sobre la versión anterior. La desinstalación conserva las preferencias del lector.

Por defecto, el instalador se genera **sin firma Authenticode**. Windows puede mostrar un aviso de editor desconocido al descargarlo. El SHA-256 permite comprobar la integridad del archivo, pero no sustituye esa firma. El flujo de firma gratuita con SignPath está preparado y requiere la aprobación del proyecto; consulta la [Code signing policy](CODE_SIGNING.md) y la [guía de configuración](signing/README.md). La preparación del flujo no implica que los binarios actuales ya estén firmados.

## Validación

```powershell
.\scripts\Test-Smoke.ps1
```

Estas pruebas ejecutan la aplicación en un dispatcher real de WinUI. Además del visor (análisis, tablas, índice, anclas, notas, búsqueda, historial, archivos y carpetas), cubren los dos editores: herramientas de formato, Return en listas, deshacer, el serializador canónico (ida y vuelta), las operaciones de Formato, el lexer por lenguaje, el mapa de scroll de Dividido, el guardado, el autoguardado y los cambios externos. El avance se escribe en `artifacts\smoke-results.progress.txt`, para ubicar una caída. El informe se guarda en `artifacts\smoke-results.json`. No usan el portapapeles del usuario ni abren navegadores, cambian asociaciones o guardan preferencias. El modo de pruebas solo existe en Debug.

### Memoria de documentos grandes

Los documentos de más de 512 KiB de bloques Markdown, o con más de 300 bloques principales, usan `ItemsRepeater` para materializar el contenido cercano a la pantalla. La búsqueda y el índice abarcan también los bloques fuera de pantalla. La virtualización funciona por bloque principal; una tabla o un párrafo individual excepcionalmente grande todavía requiere materializar ese bloque completo. La vista de código se crea al abrirla y se libera al ocultarla.

Para medir tres alternancias entre un documento grande y uno pequeño:

```powershell
.\scripts\Test-Memory.ps1 -DocumentPath "C:\Documentos\documento-grande.md"
```

El informe `artifacts\memory-results.json` separa memoria residente, memoria privada y memoria administrada. Comprueba también que se libera el renderizador anterior y el contenido de sus controles. La recolección forzada se usa exclusivamente dentro de este diagnóstico; la app normal no fuerza el GC ni recorta el conjunto de trabajo. El historial conserva hasta 20 textos de documentos para la navegación, por lo que abrir archivos nuevos repetidamente puede aumentar la memoria dentro de ese límite.

## Límites de esta versión

- Formato y Dividido admiten documentos de hasta 1 MB (en UTF-8). Uno más grande abre en Fuente; también puede leerse con formato en el visor de solo lectura, que virtualiza los bloques.
- En Formato, cada párrafo es un control propio: la selección no cruza de un párrafo a otro y las herramientas actúan sobre el párrafo actual. En Fuente sí.
- El HTML sin procesar se conserva como código fuente; `<br>` se interpreta como salto de línea. No se ejecuta JavaScript ni se incrustan páginas web.
- Las fórmulas LaTeX y los diagramas Mermaid se conservan como texto/código. No se presentan gráficamente.
- Los documentos tienen un límite de 8 MB; las imágenes, de 15 MB cada una. Se cargan hasta cuatro imágenes simultáneamente. Los formatos de imagen y las funciones SVG dependen de WinUI/Windows.
- El árbol omite archivos/carpetas ocultos, del sistema y puntos de reanálisis. F5 actualiza la lista de archivos. Los documentos abiertos desde el Explorador crean ventanas independientes.
- Las preferencias se guardan en la carpeta local de datos de la aplicación, en `Amoxtli\settings.json`.

La guía de bienvenida usa el idioma seleccionado. Los catálogos están en `Localization` y la referencia ampliada en español se conserva en `Assets\Welcome.md`. El parser Markdig se distribuye bajo BSD-2-Clause; consulta `THIRD-PARTY-NOTICES.md`.

El icono combina el símbolo de Markdown con un códice plegado de inspiración mesoamericana. El original transparente y el prompt de ImageGen están en `Assets\Branding`; `scripts\Build-IconAssets.ps1` genera el ICO y los recursos de Windows. El icono está configurado para el ejecutable, la ventana, el paquete y las asociaciones de archivos.
