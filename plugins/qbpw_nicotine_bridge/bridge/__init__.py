# SPDX-License-Identifier: GPL-3.0-or-later
"""Implementation package for the qbPortWeaver Bridge plugin.

Split out of ``__init__.py`` so the plugin entry point stays readable: the entry point owns
the Nicotine+ lifecycle contract, everything here owns the bridge itself.

Nicotine+ appends the plugin folder to ``sys.path`` before executing ``__init__.py``
(``pluginsystem.py``), so ``from bridge.x import y`` resolves. On unload it removes that path
again and purges this package from ``sys.modules``, so a disable/enable cycle re-imports
cleanly.
"""
