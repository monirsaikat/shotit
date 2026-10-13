# Language data for Bengali text recognition

These files are read by the Tesseract engine (the `Tesseract` NuGet package) and are copied beside
the program when it is built. They are unmodified copies from the Tesseract project and are
licensed under the Apache License 2.0: <https://www.apache.org/licenses/LICENSE-2.0>.

| File | From | Why this one |
| --- | --- | --- |
| `ben.traineddata` | <https://github.com/tesseract-ocr/tessdata_best> | Bengali. The "best" model reads about nine words in ten correctly at screen sizes where the "fast" one manages seven. |
| `eng.traineddata` | <https://github.com/tesseract-ocr/tessdata_fast> | English words and numbers inside Bengali text. The "fast" model is enough for that and a quarter of the size. |
